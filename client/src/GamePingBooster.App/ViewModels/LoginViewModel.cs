using System.ComponentModel;
using System.Runtime.CompilerServices;
using GamePingBooster.App.Services;

namespace GamePingBooster.App.ViewModels;

/// <summary>
/// Sign in through the browser, and fetch the first licence token for this machine.
///
/// The order is the whole point (docs/COMMERCIAL.md):
///
///     the keypair already exists      generated on first run, before any account existed
///     sign in                         in the browser - see LoopbackAuth
///     send the device PUBLIC key up   binds this machine to that account, or is refused
///
/// The device is registered at sign-in, not at generation, which is why a fresh install works
/// perfectly on a machine that has never signed in to anything.
///
/// <b>This app never sees the password.</b> The email/password form that used to live here was
/// removed on 2026-09-06 once the browser flow was confirmed working, and `/auth/login` itself was
/// deleted from the server on 2026-09-10 - it was being kept for binaries that turned out not to
/// exist. Bringing a password form back would now be a protocol change, not a UI one, and that is
/// the right way round. What is kept here is the refresh token, under DPAPI at user
/// scope; what goes down to the service is the licence token, write-only.
/// </summary>
public sealed class LoginViewModel : INotifyPropertyChanged
{
    private readonly string _licenceUrl;
    private readonly string _devicePublicKey;
    private readonly PipeClient _pipe;

    private readonly ProfileSync? _profileSync;

    public LoginViewModel(string licenceUrl, string devicePublicKey, PipeClient pipe,
        ProfileSync? profileSync = null)
    {
        _licenceUrl = licenceUrl;
        _devicePublicKey = devicePublicKey;
        _pipe = pipe;
        _profileSync = profileSync;
    }

    /// <summary>Shown so somebody can tell which server they are about to hand a password to.</summary>
    public string ServerText => $"Signing in to {_licenceUrl}";

    /// <summary>
    /// The first eight characters of the device key.
    ///
    /// Shown because "this account already has 2 devices" is unanswerable without knowing which
    /// machine you are sitting at. It is a public key, so displaying it costs nothing.
    /// </summary>
    public string DeviceText => _devicePublicKey.Length >= 8
        ? $"This device: {_devicePublicKey[..8]}... ({Environment.MachineName})"
        : $"This device: {Environment.MachineName}";

    private bool _busy;
    public bool Busy
    {
        get => _busy;
        private set => Set(ref _busy, value);
    }

    private string? _status;

    /// <summary>What the app is waiting for, while it waits. Null when it is not waiting.</summary>
    public string? Status
    {
        get => _status;
        private set
        {
            if (Set(ref _status, value)) Raise(nameof(HasStatus));
        }
    }

    public bool HasStatus => !string.IsNullOrEmpty(Status);

    private string? _error;
    public string? Error
    {
        get => _error;
        private set { if (Set(ref _error, value)) Raise(nameof(HasError)); }
    }

    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>True once a token has been stored, which is what the window closes on.</summary>
    public bool Succeeded { get; private set; }

    /// <summary>
    /// Signs in through the person's own browser. See LoopbackAuth for how, and why loopback.
    ///
    /// FinishAsync is kept separate rather than folded in here. It was shared with the password
    /// form until that came out, and it is the part that would have to be shared again the day a
    /// second way in exists - a licence key, a recovery flow, anything. Inlining it now would
    /// only have to be undone.
    /// </summary>
    public async Task SignInWithBrowserAsync(CancellationToken ct)
    {
        if (Busy) return;

        Busy = true;
        Error = null;

        LoopbackAuth? loopback = null;
        try
        {
            loopback = LoopbackAuth.Start(_licenceUrl, Environment.MachineName);

            // Said before the browser opens, not after. The window may end up behind the app, and
            // "nothing happened" is the report you get otherwise.
            Status = "Finish signing in in your browser, then come back here.";
            loopback.OpenBrowser();

            var code = await loopback.WaitForCodeAsync(ct).ConfigureAwait(true);

            using var client = new LicenceClient(_licenceUrl);
            var login = await client
                .ExchangeAsync(code, loopback.Verifier, loopback.RedirectUri, ct)
                .ConfigureAwait(true);

            await FinishAsync(client, login.RefreshToken, ct).ConfigureAwait(true);
        }
        catch (LicenceException ex)
        {
            Error = ex.Message;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Only the window closing cancels this, so in practice nobody reads it. Kept so the
            // catch below never has to guess.
            Error = "Cancelled.";
        }
        catch (LicenceTimeoutException ex)
        {
            // Only the licence server's calls throw this - LoopbackAuth's own four-minute wait for
            // the browser is a plain TimeoutException and goes to the catch below - so reaching here
            // means the browser has already said "Signed in": that page is served by our listener
            // before the exchange. The message has to say so, or the person goes looking for the
            // problem in the wrong place. Its own text carries the deadline that ran out, which
            // is 30 seconds for the exchange and 10 for the token call after it.
            Error = ex.Message + " The browser part worked - try again.";
        }
        catch (Exception ex)
        {
            // A browser that will not open, a port that cannot be claimed, a proxy in the way.
            // There is no other way in any more, so the only honest next step is another attempt.
            Error = ex.Message + " Try again.";
        }
        finally
        {
            Status = null;
            loopback?.Stop();
            Busy = false;
        }
    }

    /// <summary>
    /// Everything that happens once a refresh token exists, whichever way it was obtained.
    ///
    /// These four steps have an order and a reason for it. Written out here rather than inline so
    /// that a second way of obtaining a refresh token cannot quietly disagree about what a
    /// finished sign-in means - which is exactly what this method was extracted to prevent while
    /// the password form and the browser flow both existed.
    /// </summary>
    private async Task FinishAsync(LicenceClient client, string refreshToken, CancellationToken ct)
    {
        // The device limit is enforced HERE, on the token call, not on the sign-in. A machine
        // over the limit signs in perfectly well and then cannot get a token - which is
        // deliberate: enforcement lives where the user cannot patch it out, and refusing the
        // sign-in would punish somebody with a desktop and a laptop.
        var token = await client.FetchTokenAsync(refreshToken, _devicePublicKey,
            Environment.MachineName, ct).ConfigureAwait(true);

        // Store the refresh token only after the token call succeeded. Keeping it after a
        // refused device would leave the app quietly retrying a sign-in that cannot produce
        // anything, and the user with no idea why.
        RefreshTokenStore.Save(refreshToken);

        await _pipe.SendAsync(new Core.Ipc.CommandMessage
        {
            Verb = "set-token",
            Token = token.Token,
        }).ConfigureAwait(true);

        Succeeded = true;

        // Fetch the game list now, while the person is watching and expects something to happen.
        // Forced past the interval check for the same reason: a sign-in that leaves the ranges on
        // yesterday's copy has not really finished.
        //
        // Failures are reported, not thrown: the sign-in itself succeeded, and saying "sign in
        // failed" because a second request did would be a lie.
        if (_profileSync is not null)
        {
            // force: true, so the age of whatever profile is already stored is irrelevant and
            // there is nothing to pass for it.
            await _profileSync
                .SyncAsync(_licenceUrl, _devicePublicKey, "pubg", force: true, profileUpdatedAt: null, ct: ct)
                .ConfigureAwait(true);
        }
    }

    // --------------------------------------------------- INotifyPropertyChanged

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
