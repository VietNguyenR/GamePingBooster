using System.Net;
using GamePingBooster.Core.Ipc;
using GamePingBooster.Core.Protocol;

namespace GamePingBooster.App.Services;

/// <summary>
/// Keeps the service's licence token fresh, without anybody being asked to do anything.
///
/// The rule from docs/COMMERCIAL.md is refresh at 50% of REMAINING life, not at a fixed
/// interval. With a 24-hour token that is a fetch about every twelve hours, and the property it
/// buys is that a handshake never carries a nearly expired token: by the time a token is half
/// spent there has already been a window as long as the one left in which to replace it. A
/// licence server that is down for an afternoon is invisible.
///
/// Two things this deliberately does not do:
///
///   - it does not poll. It sleeps until the moment a refresh is due, recomputed whenever the
///     service reports a new expiry. A one-minute timer asking "is it time yet" 720 times to do
///     one HTTP call is the shape of code that ends up in a profiler.
///   - it does not touch a live tunnel. A new token applies from the next connect; the session
///     in progress was authorised when it started and the relay caps its age anyway. Dropping a
///     player out of a match to present a credential buys nothing.
/// </summary>
public sealed class TokenRefresher : IAsyncDisposable
{
    /// <summary>
    /// Never sleep longer than this in one go, however far away the deadline is.
    ///
    /// A laptop that suspends for ten hours wakes with a Task.Delay that still believes it has
    /// ten hours to run, because the delay was computed against a clock that did not advance the
    /// way the wall clock did. Waking up hourly to recheck against the real time costs nothing
    /// and removes a whole class of "it stopped refreshing overnight" reports.
    /// </summary>
    private static readonly TimeSpan MaxSleep = TimeSpan.FromHours(1);

    /// <summary>Do not hammer the server if something is wrong; back off and try again.</summary>
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long to wait after the server has ANSWERED and said no.
    ///
    /// Longer than RetryAfterFailure because the two failures are not the same kind of thing. A
    /// timeout or a DNS failure is a guess about the network and is worth revisiting soon; a 401
    /// or a 402 is a considered decision, and asking again in five minutes gets the same decision
    /// three hundred times a day.
    /// </summary>
    private static readonly TimeSpan RetryAfterRefusal = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Never schedule a refresh closer than this; wait for the expiry itself instead.
    ///
    /// Halving a remainder that is already small is a poll, not a schedule: three minutes, then
    /// ninety seconds, then forty-five. It matters now that the licence server clamps a token to
    /// the end of the subscription, because every refresh in the last hour of a subscription
    /// comes back with the SAME expiry - there is nothing left for halving to win back.
    /// </summary>
    private static readonly TimeSpan MinRefreshGap = TimeSpan.FromMinutes(5);

    private readonly PipeClient _pipe;
    private readonly Func<string?> _licenceUrl;
    private readonly Func<string?> _devicePublicKey;
    private readonly Action<string> _report;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _wake = new(0, 1);

    private Task? _loop;
    private DateTimeOffset? _expiry;

    /// <summary>
    /// When the next refresh should happen, or null when there is nothing to refresh.
    ///
    /// A DEADLINE, computed once from each expiry the service reports, rather than a delay
    /// recomputed on every pass of the loop. That distinction was a bug: NextDelay returned half
    /// of the life remaining and the loop slept for exactly that, so every pass halved the
    /// remainder and no pass ever reached zero. A refresh therefore only happened once the token
    /// had ALREADY expired - the opposite of the property the halving exists to buy, and it meant
    /// every handshake in the second half of a token's life carried one about to run out.
    /// </summary>
    private DateTimeOffset? _due;

    /// <summary>
    /// Whether this run of the app has asked the licence server anything yet.
    ///
    /// The FIRST expiry the service reports is refreshed straight away instead of in half a
    /// token's time, because a stored token says nothing about the account behind it. A
    /// subscription that lapsed while the app was closed leaves a token that is still perfectly
    /// signed and still hours from expiring, and without this the app would go on connecting
    /// with it until the ordinary half-life refresh came round - which on a 24-hour token is up
    /// to twelve hours of service nobody is paying for, and, worse, twelve hours of the app
    /// disagreeing with the account screen sitting right next to it.
    ///
    /// One request per app start. Volatile because the loop writes it and OnStatus, on the
    /// pipe's read thread, reads it; the worst a lost write costs is one extra refresh.
    /// </summary>
    private volatile bool _askedThisSession;

    public TokenRefresher(PipeClient pipe, Func<string?> licenceUrl, Func<string?> devicePublicKey,
        Action<string> report)
    {
        _pipe = pipe;
        _licenceUrl = licenceUrl;
        _devicePublicKey = devicePublicKey;
        _report = report;
    }

    public void Start() => _loop ??= Task.Run(() => LoopAsync(_cts.Token));

    /// <summary>
    /// Called on every status push. Only a CHANGED expiry wakes the loop - the service pushes a
    /// status once a second, and re-arming a timer at 1 Hz would be silly.
    /// </summary>
    public void OnStatus(StatusMessage status)
    {
        var expiry = status.TokenExpiresAt is { } unix
            ? DateTimeOffset.FromUnixTimeSeconds(unix)
            : (DateTimeOffset?)null;

        if (expiry == _expiry) return;
        _expiry = expiry;
        _due = _askedThisSession ? DueFor(expiry) : DateTimeOffset.UtcNow;

        // Release only if nothing is already pending, hence the (0, 1) semaphore: this is called
        // from the pipe's read loop and must never block or throw.
        try { _wake.Release(); } catch (SemaphoreFullException) { }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var wait = NextDelay();
            try
            {
                if (wait > TimeSpan.Zero)
                {
                    // Whichever comes first: the deadline, or a status saying the deadline moved.
                    await _wake.WaitAsync(wait, ct).ConfigureAwait(false);
                    continue;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // Move the deadline BEFORE attempting, not after. A due time left in the past is a
            // hot loop: the attempt returns, NextDelay reads the same passed deadline, and the
            // licence server is asked again with no pause at all. That is what an account whose
            // subscription had lapsed used to produce - the refresh could never succeed, so
            // nothing ever moved the deadline forward.
            //
            // A success moves it again through OnStatus, which sees the new expiry the service
            // reports and recomputes it properly.
            _due = DateTimeOffset.UtcNow + RetryAfterFailure;
            _askedThisSession = true;
            await TryRefreshAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Says so when this machine's clock is far enough out that no relay will answer it.
    ///
    /// The threshold is the relay's own window rather than something smaller, and that is the
    /// point: past it every handshake is refused, so the notice is a diagnosis and never a
    /// guess. A softer "your clock looks a bit off" tier was considered and left out - the app
    /// would be crying about a clock that still works, and the one warning that matters would be
    /// read as more of the same.
    ///
    /// Nothing is done about it automatically. Setting the system clock needs administrator
    /// rights this process does not have, and a booster that silently moves the clock is not
    /// something anybody asked for. Naming the cause is the whole job; without it the symptom is
    /// six unreachable relays and a message about the network.
    /// </summary>
    private void WarnIfClockIsWrong(TimeSpan? skew)
    {
        if (skew is not { } drift) return;
        if (drift.Duration() <= GpbProtocol.HandshakeSkew) return;

        var minutes = Math.Max(1, (int)Math.Round(drift.Duration().TotalMinutes));
        var direction = drift > TimeSpan.Zero ? "ahead of" : "behind";
        _report(
            $"This PC's clock is about {minutes} minute{(minutes == 1 ? "" : "s")} {direction} the " +
            "licence server. Relays refuse a handshake more than " +
            $"{GpbProtocol.HandshakeSkew.TotalSeconds:F0} seconds out, so connecting will fail until " +
            "the time is corrected - turn on Settings > Time & language > Set time automatically.");
    }

    /// <summary>
    /// The moment to refresh a token that runs out at <paramref name="expiry"/>: halfway through
    /// whatever life it has left right now.
    ///
    /// Half, so that by the time a refresh is due there has already been a window as long as the
    /// one still to come in which to do it - a licence server down for an afternoon is then
    /// invisible. Measured against the life LEFT rather than the full term, so a token picked up
    /// when it is already old is replaced soon rather than in twelve hours.
    /// </summary>
    private static DateTimeOffset? DueFor(DateTimeOffset? expiry)
    {
        if (expiry is not { } value) return null;

        var now = DateTimeOffset.UtcNow;
        var remaining = value - now;
        if (remaining <= TimeSpan.Zero) return now;

        var half = remaining / 2;
        return half < MinRefreshGap ? value : now + half;
    }

    /// <summary>How long until a refresh is due, clamped to <see cref="MaxSleep"/>.</summary>
    private TimeSpan NextDelay()
    {
        // Nothing to refresh: there is no token, or no server to ask. Sleep until told otherwise.
        if (_due is not { } due || string.IsNullOrWhiteSpace(_licenceUrl())) return MaxSleep;
        if (RefreshTokenStore.Load() is null) return MaxSleep;

        var wait = due - DateTimeOffset.UtcNow;
        if (wait <= TimeSpan.Zero) return TimeSpan.Zero;
        return wait > MaxSleep ? MaxSleep : wait;
    }

    /// <summary>
    /// One attempt. The caller has already armed a backoff, so nothing here has to return one -
    /// only lengthen it when the server answered rather than failed to.
    /// </summary>
    private async Task TryRefreshAsync(CancellationToken ct)
    {
        var url = _licenceUrl();
        var deviceKey = _devicePublicKey();
        var refresh = RefreshTokenStore.Load();

        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(deviceKey) || refresh is null)
        {
            // Nothing to do, and nothing wrong: self-hosted, or never signed in. Stand down
            // entirely rather than keeping the backoff the caller armed - re-deciding this every
            // five minutes for the life of the process would be a timer that can only ever reach
            // the same conclusion. A status carrying an expiry is what starts things again.
            _due = null;
            return;
        }

        try
        {
            using var client = new LicenceClient(url);
            var result = await client.FetchTokenAsync(refresh, deviceKey, Environment.MachineName, ct)
                .ConfigureAwait(false);

            await _pipe.SendAsync(new CommandMessage { Verb = "set-token", Token = result.Token })
                .ConfigureAwait(false);

            // Schedule from what the SERVER said, not from waiting for the status to come back
            // and look different. Near the end of a subscription a renewal returns the same
            // expiry it returned last time, because both are clamped to the same period end;
            // OnStatus would see no change, leave the deadline where the loop armed it, and turn
            // the last hour of every subscription into a request every five minutes.
            var renewed = DateTimeOffset.FromUnixTimeSeconds(result.ExpiresAt);
            _due = DueFor(renewed);

            _report($"Licence renewed, valid until {renewed.LocalDateTime:g}.");

            // Said AFTER the renewal line, not before: the notice is one property and the last
            // writer wins, and a broken clock is the more useful of the two to be looking at.
            WarnIfClockIsWrong(client.ClockSkew);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down. Only a genuine cancellation may leave this method by throwing.
            //
            // Without the filter an HttpClient timeout came through here too, and LoopAsync calls
            // this outside any try: one renewal slower than ten seconds ended the loop, the task
            // behind `_loop ??=` stayed finished so Start() never restarted it, and the licence
            // quietly ran out a day later with nothing in the log to say why. A timeout is a
            // network failure and is handled as one below.
            throw;
        }
        catch (LicenceException ex)
        {
            // The server answered and said no. Show its own sentence rather than inventing one,
            // and back off further than a network failure would - see RetryAfterRefusal.
            _report($"Could not renew the licence: {ex.Message}");

            // 402 is the one refusal that means the licence itself is finished: there is no
            // active subscription behind this account any more. Holding on to the token already
            // stored would leave the app offering Connect until that token ran out on its own,
            // which is precisely the gap that let a lapsed account keep connecting.
            //
            // Discarding it is not the enforcement - the relay refuses an expired token by
            // itself, and web-service no longer signs one that outlives the subscription. It is
            // what makes this side agree with them within the minute instead of within the day.
            //
            // Only 402. A 401, a 429 or a device-limit 403 are all things that can be true this
            // minute and false the next, and throwing away a working licence over one of them
            // would sign the user out of a session they were entitled to.
            if (ex.StatusCode == HttpStatusCode.PaymentRequired)
            {
                try
                {
                    await _pipe.SendAsync(new CommandMessage { Verb = "set-token", Token = null })
                        .ConfigureAwait(false);
                }
                catch (Exception clear)
                {
                    // The service being unreachable is its own visible problem; do not turn it
                    // into a second message about the licence.
                    _report($"Could not clear the expired licence ({clear.Message}).");
                }
            }

            _due = DateTimeOffset.UtcNow + RetryAfterRefusal;
        }
        catch (Exception ex)
        {
            // Network, DNS, the server being down. Worth retrying sooner, and the backoff the
            // caller armed is already that one: the whole reason for refreshing at 50% is that an
            // outage this side of the expiry does not matter.
            _report($"Could not reach the licence server ({ex.Message}). Will try again shortly.");
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
