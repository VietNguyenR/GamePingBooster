namespace GamePingBooster.Service.Tunnel;

/// <summary>
/// A licence that runs out while connected ends the connection (owner, 2026-10-22).
///
/// Before this, a trial that ended mid-session left the tunnel up: relayd's sweep does not look at token expiry, so the
/// live session carried on, but every NEW handshake was refused. The forGame move off Hong Kong then measured nothing
/// and stayed (QualityRegionPlan relaysMeasured [] - 4 of 4 stuck cases on prod, 10-08..10-20), and the player sat in a
/// lobby on the wrong relay with nothing saying why. Now the service disconnects, and says the trial is over; the app
/// offers the plans.
///
/// In the service, not the app: it holds the token and the tunnel, and it must happen with the app closed too. The
/// renewal is the app's (TokenRefresher), so "cannot be renewed" is read as: the expiry has passed, and no newer token
/// arrived within <see cref="LicenceRenewalGrace"/>. Immediately, even mid-match - the relay refuses the next reconnect
/// anyway, and a session kept alive on a licence that ended is the gap this closes.
/// </summary>
internal sealed partial class TunnelEngine
{
    /// <summary>
    /// How long past the expiry a newer token may still arrive. TokenRefresher asks AT the expiry once less than ten
    /// minutes are left (DueFor), so a paid account's renewal lands a few seconds after it; this is that round trip with
    /// room to spare, not a second chance for an account that has none.
    /// </summary>
    internal static readonly TimeSpan LicenceRenewalGrace = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The expiry of the token this connection was made with, or null when it was not made with one (self-hosted, or
    /// relays typed into config.json). Set at connect; a token pushed later only ever extends it - see
    /// <see cref="LicenceRanOut"/>.
    /// </summary>
    private DateTimeOffset? _sessionLicenceExpiry;

    /// <summary>
    /// Whether a licensed connection's licence is over: the later of the connect-time expiry and the token held now has
    /// passed by more than <see cref="LicenceRenewalGrace"/> - or at once, with no token held at all.
    ///
    /// The later of the two, because the token held now can be gone: TokenRefresher clears it on a 402, which is
    /// exactly the trial that ended, and on signing out, which the app follows with a Disconnect of its own. A clear
    /// therefore never ends a connection by itself; the connect-time expiry passing does. Past that expiry a clear is the
    /// licence server's answer already in, so there is nothing to wait for.
    /// </summary>
    internal static bool LicenceRanOut(DateTimeOffset? sessionExpiry, DateTimeOffset? heldExpiry, DateTimeOffset now)
    {
        if (sessionExpiry is not { } session) return false;
        if (heldExpiry is null) return now >= session;
        var latest = heldExpiry > session ? heldExpiry.Value : session;
        return now >= latest + LicenceRenewalGrace;
    }

    /// <summary>Remembers which licence this connection runs on. Called once a connect has succeeded.</summary>
    private void NoteSessionLicence()
    {
        var licensed = !string.IsNullOrWhiteSpace(_config.LicenceUrl) && _config.RelayEndpoints.Count == 0;
        _sessionLicenceExpiry = licensed && _token is { } token ? TokenStore.ExpiryOf(token) : null;
    }

    /// <summary>
    /// Disconnects when the licence has run out (<see cref="LicenceRanOut"/>), and says so in the status line the app
    /// turns into a notice and the plans. True when it did. Never awaited by the caller: disconnecting cancels the
    /// supervisor and waits for it, which from inside it would wait forever.
    /// </summary>
    private bool DisconnectIfLicenceRanOut()
    {
        var held = _token is { } token ? TokenStore.ExpiryOf(token) : (DateTimeOffset?)null;
        if (!LicenceRanOut(_sessionLicenceExpiry, held, DateTimeOffset.UtcNow)) return false;

        _log($"The licence ran out at {_sessionLicenceExpiry:u} and no newer one arrived - disconnecting. New handshakes " +
             "are refused from here, so a move or a reconnect could only fail.");
        _sessionLicenceExpiry = null;
        _ = Task.Run(() => DisconnectAsync(LicenceExpiredText));
        return true;
    }

    /// <summary>The line the app recognises (App.OnLicenceExpired): it shows the notice and offers the plans.</summary>
    internal static readonly StatusText LicenceExpiredText = new("svc.licenceExpired",
        "Your trial has expired - please upgrade to VIP to keep using the app.");
}
