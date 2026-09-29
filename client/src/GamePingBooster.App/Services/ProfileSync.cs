using GamePingBooster.Core.Ipc;
using GamePingBooster.Core.Profiles;
using GamePingBooster.App.Services.Localization;

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

    /// <summary>
    /// The game named in the first request of a sync. Any game the server has would do - its answer
    /// lists the rest - and PUBG is the one every licence server has had from the start.
    /// </summary>
    private const string FirstGame = "pubg";

    /// <summary>
    /// Hands one sealed profile to the service. Passed straight through: this process cannot open it
    /// and does not try - the envelope is encrypted to the device key, which lives in the service.
    /// </summary>
    private Task PushAsync(string sealedHex) =>
        _pipe.SendAsync(new CommandMessage { Verb = "set-profile", Profile = sealedHex });

    private readonly Action? _upgradeRequired;

    /// <param name="upgradeRequired">Called when the licence server refuses this version as too old (426).</param>
    public ProfileSync(PipeClient pipe, Action<string> report, Action? upgradeRequired = null)
    {
        _pipe = pipe;
        _report = report;
        _upgradeRequired = upgradeRequired;

        // What the service holds, kept current from every status it pushes - it pushes one after each store. Subscribed
        // here, before App.axaml.cs subscribes the start-up sync, so the first sync already knows.
        _pipe.StatusReceived += status =>
        {
            if (status.ProfileHashes is { } hashes)
            {
                _held = ProfileSyncPlan.Have(hashes);
                _serviceTakesMany = true;
            }
        };
    }

    /// <summary>Game id -> content hash of what the service holds; empty until its first status says.</summary>
    private volatile Dictionary<string, string> _held = new(StringComparer.Ordinal);

    /// <summary>The service reports hashes, so it knows set-profiles. False for a service older than both.</summary>
    private volatile bool _serviceTakesMany;

    /// <summary>
    /// The fetch in progress, if any. A connect pressed while the start-up sync is still fetching waits for that one
    /// instead of starting a second: two at once is what made a connect take twelve seconds on 2026-09-29, each of them
    /// fetching every game.
    /// </summary>
    private Task? _inFlight;
    private readonly Lock _gate = new();

    /// <summary>
    /// Fetches and pushes, unless it is too soon or there is nothing to fetch with.
    ///
    /// <paramref name="profileUpdatedAt"/> is when the service last wrote a pushed profile, or
    /// null if it never has. <paramref name="force"/> skips both age checks: used right after a
    /// sign-in, where the person is watching and expects something to happen.
    /// </summary>
    public async Task SyncAsync(string? licenceUrl, string? devicePublicKey,
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

        Task run;
        lock (_gate)
        {
            run = _inFlight is { IsCompleted: false } running
                ? running
                : _inFlight = FetchAndPushAsync(licenceUrl, devicePublicKey, refreshToken, ct);
        }
        await run.ConfigureAwait(false);
    }

    /// <summary>
    /// One request for every game (POST /profiles), saying what the service already holds; the server seals only what
    /// changed, and everything it sealed goes to the service in one set-profiles - which also marks the sync done when
    /// nothing changed. Against a server older than that endpoint, one request per game as before.
    /// </summary>
    private async Task FetchAndPushAsync(string licenceUrl, string devicePublicKey, string refreshToken, CancellationToken ct)
    {
        try
        {
            using var client = new LicenceClient(licenceUrl);

            // Only a service that reports hashes knows set-profiles; for an older one, ask for everything and push each.
            var many = _serviceTakesMany;
            ProfilesResult answer;
            try
            {
                answer = await client
                    .FetchProfilesAsync(refreshToken, devicePublicKey, many ? _held : new Dictionary<string, string>(), ct)
                    .ConfigureAwait(false);
            }
            catch (LicenceException ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.MethodNotAllowed)
            {
                await FetchOneByOneAsync(client, refreshToken, devicePublicKey, ct).ConfigureAwait(false);
                return;
            }

            // Recorded before the push, not after: a push that fails in the service is not a
            // reason to hammer the server again in a second.
            _lastFetch = DateTimeOffset.UtcNow;

            var envelopes = ProfileSyncPlan.EnvelopesToStore(
                answer.Profiles!.Select(p => (p.Game, p.Status, p.Envelope)));
            if (many)
            {
                await _pipe.SendAsync(new CommandMessage { Verb = "set-profiles", Profiles = envelopes }).ConfigureAwait(false);
            }
            else
            {
                foreach (var envelope in envelopes) await PushAsync(envelope).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Genuinely cancelled. LicenceClient now reports its timeouts as
            // LicenceTimeoutException, which lands in the network-failure catch below - but an
            // HttpClient timeout used to arrive here as an OperationCanceledException, and rethrown
            // from App.axaml.cs, where the call is fire-and-forget, it simply vanished: the game list
            // stayed old and nothing said so. The filter keeps that from coming back.
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
            if (ex.StatusCode == System.Net.HttpStatusCode.UpgradeRequired) _upgradeRequired?.Invoke();

            _report(Loc.F("notice.profileFailed", ex.Message));
        }
        catch (Exception ex)
        {
            // Network, DNS, server down. Not worth alarming anybody: the previous profile is
            // still in place and a profile is not urgent.
            _report(Loc.F("notice.profileUnreachable", ex.Message));
        }
    }

    /// <summary>
    /// GET /profile per game, pushed one at a time - how every sync worked before POST /profiles, kept for a licence
    /// server older than it. The first request has to name a game; its answer lists every game the server has, so a game
    /// added there reaches this client without a new release.
    /// </summary>
    private async Task FetchOneByOneAsync(LicenceClient client, string refreshToken, string devicePublicKey, CancellationToken ct)
    {
        var first = await client
            .FetchProfileAsync(refreshToken, devicePublicKey, FirstGame, ct)
            .ConfigureAwait(false);
        _lastFetch = DateTimeOffset.UtcNow;
        await PushAsync(first.Envelope).ConfigureAwait(false);

        foreach (var game in ProfileSyncPlan.LegacyGamesAfter(FirstGame, first.AvailableGames))
        {
            var next = await client
                .FetchProfileAsync(refreshToken, devicePublicKey, game, ct)
                .ConfigureAwait(false);
            await PushAsync(next.Envelope).ConfigureAwait(false);
        }
    }
}
