using GamePingBooster.Core.Ipc;

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

    private readonly PipeClient _pipe;
    private readonly Func<string?> _licenceUrl;
    private readonly Func<string?> _devicePublicKey;
    private readonly Action<string> _report;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _wake = new(0, 1);

    private Task? _loop;
    private DateTimeOffset? _expiry;

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

            if (!await TryRefreshAsync(ct).ConfigureAwait(false))
            {
                try { await Task.Delay(RetryAfterFailure, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>How long until a refresh is due, clamped to <see cref="MaxSleep"/>.</summary>
    private TimeSpan NextDelay()
    {
        // Nothing to refresh: there is no token, or no server to ask. Sleep until told otherwise.
        if (_expiry is not { } expiry || string.IsNullOrWhiteSpace(_licenceUrl())) return MaxSleep;
        if (RefreshTokenStore.Load() is null) return MaxSleep;

        var remaining = expiry - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) return TimeSpan.Zero;

        // Half of what is LEFT. Applied repeatedly this converges on the expiry rather than
        // overshooting it, and it means a token picked up when it is already old is replaced
        // soon rather than in twelve hours.
        var due = remaining / 2;
        return due > MaxSleep ? MaxSleep : due;
    }

    /// <summary>One attempt. Returns false when the caller should back off.</summary>
    private async Task<bool> TryRefreshAsync(CancellationToken ct)
    {
        var url = _licenceUrl();
        var deviceKey = _devicePublicKey();
        var refresh = RefreshTokenStore.Load();

        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(deviceKey) || refresh is null)
        {
            return true; // Nothing to do, and nothing wrong.
        }

        try
        {
            using var client = new LicenceClient(url);
            var result = await client.FetchTokenAsync(refresh, deviceKey, Environment.MachineName, ct)
                .ConfigureAwait(false);

            await _pipe.SendAsync(new CommandMessage { Verb = "set-token", Token = result.Token })
                .ConfigureAwait(false);

            _report($"Licence renewed, valid until {DateTimeOffset.FromUnixTimeSeconds(result.ExpiresAt).LocalDateTime:g}.");
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LicenceException ex)
        {
            // The server answered and said no. If the sign-in itself is dead there is no point
            // retrying with the same credential, so drop it and let the user sign in again -
            // silently retrying every five minutes forever would hide the reason from them.
            _report($"Could not renew the licence: {ex.Message}");
            return true;
        }
        catch (Exception ex)
        {
            // Network, DNS, the server being down. Worth retrying: the whole reason for
            // refreshing at 50% is that an outage this side of the expiry does not matter.
            _report($"Could not reach the licence server ({ex.Message}). Will try again shortly.");
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
