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
/// </summary>
public sealed class ProfileSync
{
    /// <summary>
    /// Do not re-fetch more often than this.
    ///
    /// The server caps a account at twelve an hour and answers 429 after that. Refusing here
    /// first means a client that is restarted repeatedly does not spend its allowance on
    /// identical answers, and never sees the 429 at all.
    /// </summary>
    private static readonly TimeSpan MinInterval = TimeSpan.FromHours(6);

    private readonly PipeClient _pipe;
    private readonly Action<string> _report;
    private DateTimeOffset _lastFetch = DateTimeOffset.MinValue;

    public ProfileSync(PipeClient pipe, Action<string> report)
    {
        _pipe = pipe;
        _report = report;
    }

    /// <summary>
    /// Fetches and pushes, unless it is too soon or there is nothing to fetch with.
    ///
    /// <paramref name="force"/> skips the interval check. Used right after a sign-in, where the
    /// person is watching and expects something to happen.
    /// </summary>
    public async Task SyncAsync(string? licenceUrl, string? devicePublicKey, string gameId,
        bool force, CancellationToken ct = default)
    {
        // Both are needed: the licence server seals the profile to this machine's device key, so
        // without the key there is nothing to seal it to and the request would be refused.
        if (string.IsNullOrWhiteSpace(licenceUrl) || string.IsNullOrWhiteSpace(devicePublicKey)) return;

        var refreshToken = RefreshTokenStore.Load();
        if (refreshToken is null) return;

        if (!force && DateTimeOffset.UtcNow - _lastFetch < MinInterval) return;

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
            // The server answered and said no. Worth showing: "no active subscription" is
            // something the person can act on, and the tunnel will keep working on the old
            // ranges meanwhile, which is exactly the sort of thing that goes unnoticed.
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
