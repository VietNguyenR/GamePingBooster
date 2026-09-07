using GamePingBooster.Core.Ipc;

namespace GamePingBooster.App.Services;

/// <summary>
/// Fetches the profile from the licence server and pushes it down to the service.
///
/// It lives in the UI for one reason: the licence server authenticates the request with the
/// account's refresh token, which is the PERSON's credential, wrapped with DPAPI at USER scope
/// in their own profile directory. The service runs as LocalSystem and cannot read it - and
/// pulling a user credential across that boundary to save a message would widen the one
/// privilege boundary this project keeps narrow. So the half that can authenticate does the
/// fetch, and the half that owns %ProgramData% does the storing.
///
/// The cost of that split is real and worth stating: **the profile only updates while the app is
/// open.** A machine whose owner never launches the UI keeps whatever it last had. That is
/// acceptable because a profile changes at most daily and the app is open whenever somebody is
/// about to play - which is exactly when a stale profile would matter.
///
/// There are exactly two callers, and knowing that is what makes MinInterval below safe to
/// reason about: the start-up pass in App.axaml.cs, which is unforced, and the pass right after
/// a sign-in, which forces. There is no periodic refresh, so an app left open for a day does not
/// pick up a server-side change until it is next started. Worth building only if that becomes a
/// real complaint; opening the app is what people do before they play.
/// </summary>
public sealed class ProfileSync
{
    /// <summary>
    /// Do not re-fetch a profile younger than this.
    ///
    /// The server caps an account at twelve an hour and answers 429 after that. Refusing here
    /// first means a client that is restarted repeatedly does not spend its allowance on
    /// identical answers, and never sees the 429 at all.
    ///
    /// That was the intent from the start and it did not work, because the "when did we last
    /// fetch" was a field on this object: it reset to MinValue on every launch, so the one case
    /// the throttle names - a client restarted repeatedly - was the one case it could not
    /// prevent. Twelve launches in an hour is an ordinary afternoon here, and the twelfth got
    /// "Too many requests. Try again later" for doing nothing but opening the app.
    ///
    /// The age now comes from the SERVICE, which reports when the pushed profile was last
    /// written. That answer outlives this process, which is the entire requirement.
    ///
    /// **Ten minutes, and it used to be six hours.** This is the ONLY thing gating the start-up
    /// sync - there is no periodic refresh and no other unforced caller - so six hours did not
    /// mean "refresh every six hours", it meant a profile edited on the server was invisible for
    /// up to six hours no matter how many times the app was reopened. Signing out and back in
    /// was the only way to see a change, because that path forces. Reported from a real edit on
    /// 2026-09-07: relays changed in the database, and reopening the app kept the old ones.
    ///
    /// Ten minutes is picked against the server's own cap rather than by feel: the gate is on
    /// the age of the STORED profile, which only moves on a successful fetch, so the worst case
    /// is six fetches an hour - half the allowance of twelve, leaving the rest for the forced
    /// sync after a sign-in. Restarting the app in a loop still cannot reach a 429.
    /// </summary>
    private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(10);

    private readonly PipeClient _pipe;
    private readonly Action<string> _report;

    /// <summary>
    /// Within-process guard, kept as well as the age check rather than instead of it.
    ///
    /// The service's timestamp only moves once set-profile has been received and written, so two
    /// syncs raised in quick succession - a sign-in and the start-up sync, say - would both see
    /// the old one. This closes that window; the age check closes the restart one. Neither
    /// subsumes the other.
    /// </summary>
    private DateTimeOffset _lastFetch = DateTimeOffset.MinValue;

    public ProfileSync(PipeClient pipe, Action<string> report)
    {
        _pipe = pipe;
        _report = report;
    }

    /// <summary>
    /// Fetches and pushes, unless it is too soon or there is nothing to fetch with.
    ///
    /// <paramref name="profileUpdatedAt"/> is when the service last wrote a pushed profile, or
    /// null if it never has. <paramref name="force"/> skips both age checks: used right after a
    /// sign-in, where the person is watching and expects something to happen.
    /// </summary>
    public async Task SyncAsync(string? licenceUrl, string? devicePublicKey, string gameId,
        bool force, DateTimeOffset? profileUpdatedAt = null, CancellationToken ct = default)
    {
        // Both are needed: the licence server seals the profile to this machine's device key, so
        // without the key there is nothing to seal it to and the request would be refused.
        if (string.IsNullOrWhiteSpace(licenceUrl) || string.IsNullOrWhiteSpace(devicePublicKey)) return;

        var refreshToken = RefreshTokenStore.Load();
        if (refreshToken is null) return;

        if (!force)
        {
            if (DateTimeOffset.UtcNow - _lastFetch < MinInterval) return;

            // The profile the service already holds is recent enough. Asking again would get the
            // same bytes back and spend one of twelve requests an hour to do it.
            if (profileUpdatedAt is { } written && DateTimeOffset.UtcNow - written < MinInterval) return;
        }

        try
        {
            using var client = new LicenceClient(licenceUrl);
            var sealedHex = await client
                .FetchProfileAsync(refreshToken, devicePublicKey,
                    string.IsNullOrWhiteSpace(gameId) ? "pubg" : gameId, ct)
                .ConfigureAwait(false);

            // Recorded before the push, not after: a push that fails in the service is not a
            // reason to hammer the server again in a second.
            _lastFetch = DateTimeOffset.UtcNow;

            // Passed straight through. This process cannot open it and does not try: the
            // envelope is encrypted to the device key, which lives in the service.
            await _pipe.SendAsync(new CommandMessage { Verb = "set-profile", Profile = sealedHex })
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LicenceException ex)
        {
            // 429 is the one refusal not worth putting in front of anybody. It says the account
            // has asked a lot recently, it fixes itself within the hour, the tunnel is working on
            // the profile already stored, and there is nothing the person could do about it if
            // they wanted to. Treating it like "no active subscription" - which is what happened
            // - turns a throttle into an alarm.
            //
            // Everything else IS worth showing. "No active subscription" is actionable, and the
            // tunnel quietly running on months-old ranges is exactly the sort of thing that goes
            // unnoticed.
            if (ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests) return;

            _report($"Could not update the game list: {ex.Message}");
        }
        catch (Exception ex)
        {
            // Network, DNS, server down. Not worth alarming anybody: the previous profile is
            // still in place and a profile is not urgent.
            _report($"Could not reach the licence server to update the game list ({ex.Message}).");
        }
    }
}
