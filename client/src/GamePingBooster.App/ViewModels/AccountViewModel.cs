using System.ComponentModel;
using System.Runtime.CompilerServices;
using GamePingBooster.App.Services;
using GamePingBooster.Core.Ipc;

namespace GamePingBooster.App.ViewModels;

/// <summary>
/// What the account screen shows once somebody is signed in, and the sign-out.
///
/// It replaces the old behaviour, which was to reopen the sign-in window on a machine that was
/// already signed in - a dead end that asked for a password to reach a state it was already in.
/// </summary>
public sealed class AccountViewModel : INotifyPropertyChanged
{
    private readonly string _licenceUrl;
    private readonly string _devicePublicKey;
    private readonly PipeClient _pipe;

    public AccountViewModel(string licenceUrl, string devicePublicKey, PipeClient pipe)
    {
        _licenceUrl = licenceUrl;
        _devicePublicKey = devicePublicKey;
        _pipe = pipe;
    }

    public string ServerText => _licenceUrl;

    public string DeviceText => _devicePublicKey.Length >= 10
        ? $"{Environment.MachineName} - {_devicePublicKey[..10]}..."
        : Environment.MachineName;

    private bool _loading = true;
    public bool Loading
    {
        get => _loading;
        private set { if (Set(ref _loading, value)) Raise(nameof(Ready)); }
    }

    public bool Ready => !Loading;

    private string _email = "";
    public string Email { get => _email; private set => Set(ref _email, value); }

    private string _plan = "-";
    public string Plan { get => _plan; private set => Set(ref _plan, value); }

    private string _status = "-";
    public string Status { get => _status; private set => Set(ref _status, value); }

    private string _expires = "-";
    public string Expires { get => _expires; private set => Set(ref _expires, value); }

    private string _devices = "-";
    public string Devices { get => _devices; private set => Set(ref _devices, value); }

    private string? _error;
    public string? Error
    {
        get => _error;
        private set { if (Set(ref _error, value)) Raise(nameof(HasError)); }
    }

    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>Set once sign-out has finished, so the window can close itself.</summary>
    public bool SignedOut { get; private set; }

    public async Task LoadAsync(CancellationToken ct)
    {
        Loading = true;
        Error = null;
        try
        {
            var refreshToken = RefreshTokenStore.Load();
            if (refreshToken is null)
            {
                Error = "This machine is not signed in any more. Close this and sign in again.";
                return;
            }

            using var client = new LicenceClient(_licenceUrl);
            var account = await client.FetchAccountAsync(refreshToken, ct).ConfigureAwait(true);

            Email = account.Email;
            Plan = account.Plan ?? "No plan";
            Status = account.Status switch
            {
                "TRIALING" => "Trial",
                "ACTIVE" => "Active",
                "PAST_DUE" => "Payment overdue",
                "CANCELLED" => "Cancelled",
                "EXPIRED" => "Expired",
                null => "No subscription",
                _ => account.Status,
            };
            Expires = account.ExpiresAt is { } unix
                ? DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime.ToString("g")
                : "-";
            Devices = account.DeviceLimit > 0
                ? $"{account.DeviceCount} of {account.DeviceLimit}"
                : account.DeviceCount.ToString();
        }
        catch (LicenceException ex)
        {
            Error = ex.Message;
        }
        catch (OperationCanceledException)
        {
            // The window closed. Nothing to report to.
        }
        catch (Exception ex)
        {
            Error = $"Could not reach {_licenceUrl}: {ex.Message}";
        }
        finally
        {
            Loading = false;
        }
    }

    /// <summary>
    /// Signs out of this machine.
    ///
    /// Five things happen, in an order chosen so that a failure part way through still leaves
    /// the machine signed out rather than half signed in:
    ///
    ///   1. the server is asked to revoke the credential - best effort, because somebody on a
    ///      dead network still has the right to sign out of their own computer
    ///   2. the refresh token is deleted locally, which is what actually ends the session here
    ///   3. the licence token is cleared from the service, so nothing presents it again
    ///   4. a tunnel that is up is brought down
    ///   5. the sealed profile is left alone
    ///
    /// **Point 4 replaces the opposite decision**, which was to let a live tunnel finish on the
    /// grounds that the relay had already authorised that session and dropping somebody out of a
    /// match would be worse. That reasoning was about the relay and ignored the person: signing
    /// out and then watching the app go on saying "Connected" does not read as a considerate
    /// choice, it reads as a sign-out that did not work. If somebody wants to keep playing they
    /// have not pressed Sign out. Best effort, like the revoke: the credential is already gone
    /// from disk by this point, and a service that cannot be reached must not turn a completed
    /// sign-out into an error.
    ///
    /// Point 5 is a decision too. The profile is sealed to this machine's device key and is
    /// useless to anybody else; deleting it would mean a person who signs back in cannot connect
    /// until they are online again, which punishes the wrong case. Signing out is not "leave no
    /// trace" - it is "stop using this account".
    /// </summary>
    public async Task SignOutAsync(CancellationToken ct)
    {
        Loading = true;
        Error = null;
        try
        {
            var refreshToken = RefreshTokenStore.Load();
            if (refreshToken is not null)
            {
                try
                {
                    using var client = new LicenceClient(_licenceUrl);
                    await client.LogoutAsync(refreshToken, ct).ConfigureAwait(true);
                }
                catch (Exception)
                {
                    // Best effort by design. The local half below is what matters.
                }
            }

            RefreshTokenStore.Clear();

            // Null clears it, so nothing presents this account's licence again.
            await _pipe.SendAsync(new CommandMessage { Verb = "set-token", Token = null })
                .ConfigureAwait(true);

            // And bring down anything running on it. Unconditional rather than checked first:
            // this window does not receive the status, so asking "is it connected" would mean
            // wiring the tunnel state through to a screen that has no other use for it. The verb
            // is idempotent - the service returns immediately when there is nothing to tear
            // down - so sending it always is both simpler and correct.
            await _pipe.DisconnectTunnelAsync().ConfigureAwait(true);

            SignedOut = true;
        }
        catch (Exception ex)
        {
            Error = $"Signed out on this machine, but the service did not confirm: {ex.Message}";
            // Still true: the credential is gone from disk, which is the part that matters.
            SignedOut = true;
        }
        finally
        {
            Loading = false;
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
