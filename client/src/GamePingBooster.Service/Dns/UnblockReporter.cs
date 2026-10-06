using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using GamePingBooster.Core.Net;
using GamePingBooster.Service.Discovery;
using GamePingBooster.Service.Native;

namespace GamePingBooster.Service.Dns;

/// <summary>
/// Sends what <see cref="UnblockDiagnosis"/> finds when unblocking goes wrong to the licence server's
/// /unblock-reports, where it is read on /admin/unblock/reports - so the next provider that starts blocking a game
/// shows up there with its checks and its log, instead of as a player on a black screen in a support chat.
///
/// Three things set it off, each one a failure seen on 2026-10-02:
///
///   enable-failed   unblocking would not turn on (an FPT machine's self-test timed out every minute)
///   filtered        a claimed name has no edge that completes a handshake, and on its own addresses the line
///                   cuts its handshake while another name's completes - cutting it by name (FPT, Viettel)
///   black-screen    a game is running and a service's canary gives Windows no usable address
///
/// At most one report per trigger per network every <see cref="Quiet"/>, and only under the Settings switch that
/// covers connection quality and discovery, signed by this device like them. Nothing is kept on disk: a report not
/// sent before the service stops is not worth sending later, the line it describes may be gone.
/// </summary>
internal sealed class UnblockReporter : IDisposable
{
    public const string Domain = "gpb-unblock-v1\n";

    /// <summary>The same trouble on the same network is said again no sooner than this.</summary>
    private static readonly TimeSpan Quiet = TimeSpan.FromHours(6);

    /// <summary>How often the running game's lobby name is looked at through Windows.</summary>
    private static readonly TimeSpan WatchEvery = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long after the trouble the line is checked - see ReportAsync.</summary>
    private static readonly TimeSpan SettleFirst = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan[] Retries = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30)];

    private static readonly string? AppVersion = typeof(UnblockReporter).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    private readonly Func<string?> _licenceUrl;
    private readonly Func<byte[]?> _token;
    private readonly ECDsa _deviceKey;
    private readonly Func<bool> _allowed;
    private readonly Action<string> _log;
    private readonly Func<UnblockPolicy> _policy;
    private readonly Func<(bool Enabled, string? LastError)> _state;
    private readonly IUnblockRoutes? _routes;
    private readonly Func<bool> _gameRunning;
    private readonly string? _logPath;

    private readonly ConcurrentDictionary<string, DateTime> _lastSaid = new();
    private readonly ConcurrentQueue<(string Trigger, string Detail, string? Name)> _pending = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly HttpClient _http;
    private readonly Task _loop;
    private readonly Task _watch;

    public UnblockReporter(
        Func<string?> licenceUrl, Func<byte[]?> token, ECDsa deviceKey, Func<bool> allowed, Action<string> log,
        Func<UnblockPolicy> policy, Func<(bool Enabled, string? LastError)> state, IUnblockRoutes? routes,
        Func<bool> gameRunning, string? logPath)
    {
        _licenceUrl = licenceUrl;
        _token = token;
        _deviceKey = deviceKey;
        _allowed = allowed;
        _log = log;
        _policy = policy;
        _state = state;
        _routes = routes;
        _gameRunning = gameRunning;
        _logPath = logPath;

        _http = new HttpClient(new SocketsHttpHandler { ConnectCallback = HappyEyeballs.ConnectCallback }, disposeHandler: true)
        {
            Timeout = RequestTimeout,
        };
        _loop = Task.Run(() => RunAsync(_cts.Token));
        _watch = Task.Run(() => WatchAsync(_cts.Token));
    }

    /// <summary>
    /// Something went wrong. Cheap and never blocks: called from the resolver's own threads. Dropped at once when
    /// sharing is off, when this trouble was already said for this network within <see cref="Quiet"/>, or when one is
    /// already waiting.
    /// </summary>
    public void Trouble(string trigger, string detail, string? name)
    {
        if (!_allowed()) return;

        var key = trigger + "|" + NetworkSignature();
        var now = DateTime.UtcNow;
        if (_lastSaid.TryGetValue(key, out var last) && now - last < Quiet) return;
        _lastSaid[key] = now;

        _pending.Enqueue((trigger, detail, name));
        _wake.Release();
    }

    /// <summary>The resolvers this machine uses: a different network is a different line, and worth its own report.</summary>
    private static string NetworkSignature()
    {
        try
        {
            return string.Join(",", AdapterDns.Read().Select(a => a.ToString()).Order(StringComparer.Ordinal));
        }
        catch (Exception)
        {
            return "?";
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _wake.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            while (_pending.TryDequeue(out var trouble))
            {
                try
                {
                    await ReportAsync(trouble.Trigger, trouble.Detail, trouble.Name, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _log($"Unblock report: could not check or send ({ex.Message}).");
                }
            }
        }
    }

    /// <summary>
    /// The black screen, looked for: while a game runs, every service's canary through Windows. Without a flush -
    /// what is cached is exactly what the game is being handed.
    /// </summary>
    private async Task WatchAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(WatchEvery, ct).ConfigureAwait(false);
                // Not connected, unblocking is off on purpose for a licensed installation - the ISP's lie is
                // expected then, and reporting it would be noise. A self-hosted one has no tunnel and is always on.
                if (!_allowed() || !_gameRunning() || (_routes is not null && !_routes.Ready && !_state().Enabled)) continue;

                foreach (var canary in _policy().Apps.Select(a => a.Canary).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (await UnusableThroughWindowsAsync(canary, ct).ConfigureAwait(false))
                    {
                        Trouble("black-screen", $"a game is running and {canary} gives Windows no usable address", canary);
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // Looking is best effort; the next minute looks again.
            }
        }
    }

    private static async Task<bool> UnusableThroughWindowsAsync(string name, CancellationToken ct)
    {
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            var addresses = await System.Net.Dns.GetHostAddressesAsync(name, System.Net.Sockets.AddressFamily.InterNetwork, limit.Token)
                .ConfigureAwait(false);
            // The split's address is loopback and works - see SplitProxy. On 2026-10-06 a hosts line pointing the lobby at
            // the test proxy was reported as a black screen while the lobby loaded.
            return addresses.Length == 0 || addresses.All(a =>
                (System.Net.IPAddress.IsLoopback(a) && !SplitProxy.IsOurs(a)) || a.Equals(System.Net.IPAddress.Any));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // "No such host" is exactly the black screen.
            return true;
        }
    }

    private async Task ReportAsync(string trigger, string detail, string? name, CancellationToken ct)
    {
        var (enabled, lastError) = _state();
        var context = new UnblockDiagnosis.Context(trigger, detail, name, _policy(), enabled, lastError, _routes, _logPath);

        // A moment for the resolver to settle: "filtered" is said from inside a lookup, often while unblocking is
        // still turning on, and a check run that instant reads a half-built state.
        await Task.Delay(SettleFirst, ct).ConfigureAwait(false);
        _log($"Unblock report: {trigger} ({detail}) - checking the line before sending.");
        var findings = await UnblockDiagnosis.RunAsync(context, ct).ConfigureAwait(false);
        _log($"Unblock report: {findings.Verdict} - {findings.Summary}");

        var body = Body(Guid.NewGuid().ToString("D"), context, findings, DateTimeOffset.UtcNow);

        foreach (var wait in Retries.Prepend(TimeSpan.Zero))
        {
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
            if (!_allowed()) return;

            var url = _licenceUrl();
            var token = _token();
            if (string.IsNullOrWhiteSpace(url) || token is null) continue;

            using var request = new HttpRequestMessage(HttpMethod.Post, url.TrimEnd('/') + "/unblock-reports")
            {
                Content = new ByteArrayContent(body),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Add("X-Gpb-Licence", Convert.ToHexStringLower(token));
            request.Headers.Add("X-Gpb-Signature", DiscoveryReport.Sign(_deviceKey, body, Domain));

            try
            {
                using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                if (response.IsSuccessStatusCode || status == 429)
                {
                    _log($"Unblock report: sent ({status}).");
                    return;
                }
                if (status is 400 or 413)
                {
                    _log($"Unblock report: the licence server will not take it ({status}) - dropped.");
                    return;
                }
                _log($"Unblock report: the licence server answered {status} - trying again later.");
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _log($"Unblock report: could not reach the licence server ({ex.Message}) - trying again later.");
            }
        }
        _log("Unblock report: given up on this one.");
    }

    internal static byte[] Body(string id, UnblockDiagnosis.Context context, UnblockDiagnosis.Findings f, DateTimeOffset seenAt)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("id", id);
            json.WriteString("trigger", context.Trigger);
            json.WriteString("verdict", f.Verdict);
            json.WriteStartArray("flags");
            foreach (var flag in f.Flags) json.WriteStringValue(flag);
            json.WriteEndArray();
            json.WriteString("summary", f.Summary);
            json.WriteString("seenAt", seenAt.UtcDateTime.ToString("O"));
            if (AppVersion is not null) json.WriteString("appVersion", AppVersion);
            json.WritePropertyName("report");
            UnblockDiagnosis.WriteChecks(json, f, context);
            json.WriteString("log", f.Log);
            json.WriteEndObject();
        }
        return stream.ToArray();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            Task.WaitAll([_loop, _watch], TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }
        _http.Dispose();
        _cts.Dispose();
        _wake.Dispose();
    }
}
