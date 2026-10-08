using GamePingBooster.App.Services.Localization;
using GamePingBooster.Core.Ipc;

namespace GamePingBooster.App.Services;

/// <summary>
/// Notices, within a second or two, that this machine has been signed out because the account signed in on
/// another one - "last sign-in wins" (owner's call, 2026-10-08) - and hands that to the app, which disconnects
/// FIRST and only then says why.
///
/// One long poll of GET /auth/watch at a time, for as long as this machine is signed in, connected or not: the
/// server holds it for 45 s and answers the moment the sign-in is ended, so a machine that lost its seat in the
/// middle of a match stops using it at once instead of at the next renewal, half a day later. The relay cuts the
/// session on its own within ~20 s (heartbeat `revoked`); this is what brings the app down with it and tells the
/// person, rather than leaving them on a "Reconnecting..." that can never succeed.
///
/// In the app and not the service for the same reason as TokenRefresher: the refresh token is the person's and
/// never crosses the pipe.
///
/// What it does NOT do is decide anything. The server has already revoked the sign-in by the time this hears of
/// it; a cracked copy that ignores the answer still has no token the relay or the licence server will take.
/// </summary>
public sealed class SignInWatcher : IAsyncDisposable
{
    /// <summary>The <c>code</c> the licence server sends with a sign-in ended by one on another machine.</summary>
    public const string SignedInElsewhereCode = "signed_in_elsewhere";

    /// <summary>
    /// Asked again at least this often while there is nothing to watch with - no licence server yet, or not signed
    /// in. A sign-in wakes the loop straight away (<see cref="Wake"/>); this is only the fallback.
    /// </summary>
    private static readonly TimeSpan IdleRecheck = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The longest a failing watch backs off to. Retries are spread at random up to the backoff (0-10 s first), so a
    /// web-service restart does not get every app online back in the same second.
    /// </summary>
    private static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    /// <summary>
    /// After the server answered and said no in a way asking again will not change soon: the sign-in is gone for
    /// some other reason (<c>signed_out</c>), or the server is older than the route (404). Neither is a reason to
    /// tear anything down here - the renewal and the relay already deal with a sign-in that is simply over.
    /// </summary>
    private static readonly TimeSpan RetryAfterRefusal = TimeSpan.FromMinutes(10);

    /// <summary>A 200 that came back faster than this was not held - a proxy in the way, say. Never a hot loop.</summary>
    private static readonly TimeSpan MinHeld = TimeSpan.FromSeconds(5);

    private readonly Action<SignedOutElsewhere> _signedOut;
    private readonly Func<string?> _refreshToken;
    private readonly Func<string, LicenceClient> _clientFor;
    private readonly Action<string>? _log;

    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly object _gate = new();
    private CancellationTokenSource? _poll;
    private Task? _loop;

    private volatile string? _licenceUrl;

    /// <summary>The refresh token the app has already been told about, so one eviction is reported once.</summary>
    private string? _reportedFor;

    /// <param name="signedOut">
    /// Called once per ended sign-in, from a background thread: disconnect, sign out locally, then show it.
    /// </param>
    /// <param name="refreshToken">The signed-in credential; RefreshTokenStore.Load in the app.</param>
    /// <param name="clientFor">Makes the HTTP client for a licence URL; replaceable for a check.</param>
    public SignInWatcher(Action<SignedOutElsewhere> signedOut, Func<string?>? refreshToken = null,
        Func<string, LicenceClient>? clientFor = null, Action<string>? log = null)
    {
        _signedOut = signedOut;
        _refreshToken = refreshToken ?? RefreshTokenStore.Load;
        _clientFor = clientFor ?? (url => new LicenceClient(url));
        _log = log;
    }

    public void Start() => _loop ??= Task.Run(() => LoopAsync(_cts.Token));

    /// <summary>
    /// Takes the licence server from the status push. Only a CHANGED one wakes the loop: the service pushes once a
    /// second, and a wake ends the poll in flight.
    /// </summary>
    public void OnStatus(StatusMessage status)
    {
        var url = string.IsNullOrWhiteSpace(status.LicenceUrl) ? null : status.LicenceUrl;
        if (url == _licenceUrl) return;
        _licenceUrl = url;
        Wake();
    }

    /// <summary>
    /// Watches again from scratch, now: called after a sign-in, so the new credential is watched at once instead of
    /// after the old one's poll runs out. Ends the poll in flight.
    /// </summary>
    public void Wake()
    {
        lock (_gate) _poll?.Cancel();
        try { _wake.Release(); } catch (SemaphoreFullException) { }
    }

    /// <summary>
    /// Another call - a renewal, the account screen - was refused with <c>signed_in_elsewhere</c>. Treated exactly as
    /// the watch saying so; what an app that was off when it happened meets first. Anything else is ignored.
    /// </summary>
    public void Report(LicenceException ex)
    {
        if (!ex.SignedInElsewhere) return;
        Fire(_refreshToken(), new SignedOutElsewhere(ex.OtherDevice, ex.SignedOutAt));
    }

    private void Fire(string? token, SignedOutElsewhere what)
    {
        lock (_gate)
        {
            // The credential this was about is no longer the one held - signed out and in again since the request
            // left. Telling that person they were signed out would be wrong.
            if (token is null || token != _refreshToken()) return;
            if (token == _reportedFor) return;
            _reportedFor = token;
        }
        _log?.Invoke("Signed out: the account signed in on another machine.");
        try
        {
            _signedOut(what);
        }
        catch (Exception)
        {
            // The handler owns its own failures; this loop must survive them.
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            // A wake from before this poll is answered by this poll. Left pending, it would cut the pause after it to
            // nothing - two requests where one was meant. A wake DURING the poll ends the poll instead (see Wake).
            _wake.Wait(0);

            var url = _licenceUrl;
            var token = _refreshToken();
            if (url is null || token is null || token == _reportedFor)
            {
                if (!await SleepAsync(IdleRecheck, ct).ConfigureAwait(false)) return;
                continue;
            }

            TimeSpan pause;
            using (var poll = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                lock (_gate) _poll = poll;
                var started = DateTimeOffset.UtcNow;
                try
                {
                    using var client = _clientFor(url);
                    var result = await client.WatchSignInAsync(token, poll.Token).ConfigureAwait(false);
                    failures = 0;

                    switch (result.State)
                    {
                        case "signed_in_elsewhere":
                            Fire(token, new SignedOutElsewhere(result.DeviceLabel,
                                result.At is { } at ? DateTimeOffset.FromUnixTimeSeconds(at) : null));
                            pause = TimeSpan.Zero;
                            break;
                        case "signed_out":
                            pause = RetryAfterRefusal;
                            break;
                        default:
                            var held = DateTimeOffset.UtcNow - started;
                            pause = TimeSpan.FromSeconds(Math.Clamp(result.Wait, 0, 600));
                            if (held < MinHeld && pause < MinHeld) pause = Jitter(FirstBackoff, MinHeld);
                            break;
                    }
                }
                catch (OperationCanceledException) when (poll.IsCancellationRequested)
                {
                    // Shutting down, or woken for a new sign-in: either way, round again (the while sees the first).
                    pause = TimeSpan.Zero;
                }
                catch (LicenceException ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound
                                                      or System.Net.HttpStatusCode.MethodNotAllowed)
                {
                    // A licence server older than the route. Nothing to watch; the renewal still says it eventually.
                    pause = RetryAfterRefusal;
                }
                catch (Exception)
                {
                    // Network, a timeout, a 5xx, a deploy restarting the server. Spread out, longer each time.
                    failures++;
                    var cap = TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks,
                        FirstBackoff.Ticks << Math.Min(failures - 1, 10)));
                    pause = Jitter(cap, TimeSpan.FromSeconds(1));
                }
                finally
                {
                    lock (_gate) if (_poll == poll) _poll = null;
                }
            }

            if (pause > TimeSpan.Zero && !await SleepAsync(pause, ct).ConfigureAwait(false)) return;
        }
    }

    /// <summary>A random wait between <paramref name="floor"/> and <paramref name="cap"/>.</summary>
    private static TimeSpan Jitter(TimeSpan cap, TimeSpan floor) =>
        floor + TimeSpan.FromTicks((long)(Random.Shared.NextDouble() * Math.Max(0, (cap - floor).Ticks)));

    /// <summary>Sleeps until <paramref name="wait"/> passes or something wakes the loop. False when shutting down.</summary>
    private async Task<bool> SleepAsync(TimeSpan wait, CancellationToken ct)
    {
        try
        {
            await _wake.WaitAsync(wait, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _cts.Dispose();
        _wake.Dispose();
    }
}

/// <summary>Who signed this machine out, as far as the server knows: the other machine's label and the moment.</summary>
public sealed record SignedOutElsewhere(string? OtherDevice, DateTimeOffset? At)
{
    /// <summary>
    /// The sentence for the person, in the app's language. Built here rather than taken from the server's
    /// <c>error</c>, which is Vietnamese only; the time is this PC's, as "HH:mm dd/MM" like the server's.
    /// </summary>
    public string Text()
    {
        var device = string.IsNullOrWhiteSpace(OtherDevice) ? "" : Loc.F("signedOut.elsewhere.device", OtherDevice.Trim());
        var at = At is { } when
            ? Loc.F("signedOut.elsewhere.at",
                when.ToLocalTime().ToString("HH:mm dd/MM", System.Globalization.CultureInfo.InvariantCulture))
            : "";
        return Loc.F("signedOut.elsewhere", device, at);
    }
}
