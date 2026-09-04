using System.ComponentModel;
using System.Runtime.CompilerServices;
using GamePingBooster.App.Services;

namespace GamePingBooster.App.ViewModels;

/// <summary>
/// Sign in, and fetch the first licence token for this machine.
///
/// Two calls, in this order, and the order is the whole point (docs/COMMERCIAL.md):
///
///     the keypair already exists      generated on first run, before any account existed
///     sign in                         proves who the person is
///     send the device PUBLIC key up   binds this machine to that account, or is refused
///
/// The device is registered at sign-in, not at generation, which is why a fresh install works
/// perfectly on a machine that has never signed in to anything.
///
/// The password is never stored and never leaves this window. What is kept is the refresh token,
/// under DPAPI at user scope; what goes down to the service is the licence token, write-only.
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

    private string _email = string.Empty;
    public string Email
    {
        get => _email;
        set { if (Set(ref _email, value)) { Raise(nameof(CanSubmit)); Error = null; } }
    }

    private string _password = string.Empty;
    public string Password
    {
        get => _password;
        set { if (Set(ref _password, value)) { Raise(nameof(CanSubmit)); Error = null; } }
    }

    private bool _busy;
    public bool Busy
    {
        get => _busy;
        private set { if (Set(ref _busy, value)) { Raise(nameof(CanSubmit)); Raise(nameof(ButtonText)); } }
    }

    public string ButtonText => Busy ? "Signing in..." : "Sign in";

    public bool CanSubmit => !Busy
        && !string.IsNullOrWhiteSpace(Email)
        && !string.IsNullOrEmpty(Password);

    private string? _error;
    public string? Error
    {
        get => _error;
        private set { if (Set(ref _error, value)) Raise(nameof(HasError)); }
    }

    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>True once a token has been stored, which is what the window closes on.</summary>
    public bool Succeeded { get; private set; }

    public async Task SubmitAsync(CancellationToken ct)
    {
        if (!CanSubmit) return;

        Busy = true;
        Error = null;
        try
        {
            using var client = new LicenceClient(_licenceUrl);

            var login = await client.LoginAsync(Email.Trim(), Password, ct).ConfigureAwait(true);

            // The device limit is enforced HERE, on the token call, not on the login above. A
            // machine over the limit signs in perfectly well and then cannot get a token - which
            // is deliberate: enforcement lives where the user cannot patch it out, and refusing
            // the login would punish somebody with a desktop and a laptop.
            var token = await client.FetchTokenAsync(login.RefreshToken, _devicePublicKey,
                Environment.MachineName, ct).ConfigureAwait(true);

            // Store the refresh token only after the token call succeeded. Keeping it after a
            // refused device would leave the app quietly retrying a sign-in that cannot produce
            // anything, and the user with no idea why.
            RefreshTokenStore.Save(login.RefreshToken);

            await _pipe.SendAsync(new Core.Ipc.CommandMessage
            {
                Verb = "set-token",
                Token = token.Token,
            }).ConfigureAwait(true);

            // The password has done its job. It was never stored; this drops it from memory too,
            // and from the box on screen.
            Password = string.Empty;
            Succeeded = true;

            // Fetch the game list now, while the person is watching and expects something to
            // happen. Forced past the interval check for the same reason: a sign-in that leaves
            // the ranges on yesterday's copy has not really finished.
            //
            // Failures are reported, not thrown: the sign-in itself succeeded, and saying "sign
            // in failed" because a second request did would be a lie.
            if (_profileSync is not null)
            {
                await _profileSync.SyncAsync(_licenceUrl, _devicePublicKey, "pubg", true, ct)
                    .ConfigureAwait(true);
            }
        }
        catch (LicenceException ex)
        {
            Error = ex.Message;
        }
        catch (OperationCanceledException)
        {
            Error = "Cancelled.";
        }
        catch (Exception ex)
        {
            Error = $"Could not reach {_licenceUrl}: {ex.Message}";
        }
        finally
        {
            Busy = false;
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
