using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace GamePingBooster.App.ViewModels;

/// <summary>
/// The relay settings screen: an address, a key, and an optional label.
///
/// It holds no configuration of its own and reads no file. The UI runs as a normal user and the
/// service's configuration lives in %ProgramData%, which that user cannot write - so the values
/// go down the pipe and the service, which owns the file, writes them. See the set-relay verb.
///
/// The key is never populated from the service, only sent to it. Anything the service could send
/// back up would be readable by any process running as the user, because the pipe is open to
/// BuiltinUsers. So an already-configured installation shows an empty key box and says the key
/// is already set, rather than displaying it.
/// </summary>
public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private readonly bool _alreadyConfigured;

    public SettingsViewModel(IEnumerable<string>? currentEndpoints, bool alreadyConfigured,
        string? currentLicenceUrl = null)
    {
        _endpoints = string.Join(Environment.NewLine, currentEndpoints ?? []);
        _alreadyConfigured = alreadyConfigured;
        _licenceUrl = currentLicenceUrl ?? string.Empty;
    }

    /// <summary>
    /// Where to sign in. Empty means self-hosted, which is the default.
    ///
    /// It is here because there was nowhere else. Without it the sign-in button could only ever
    /// be revealed by editing config.json as an administrator, which meant the whole licensed
    /// path was unreachable from the product itself - the screen existed and nothing could open
    /// it. Unlike the key, this IS read back from the service: it is an address, not a secret.
    /// </summary>
    private string _licenceUrl;
    public string LicenceUrl
    {
        get => _licenceUrl;
        set { if (Set(ref _licenceUrl, value)) { Raise(nameof(CanSave)); Saved = false; } }
    }

    /// <summary>
    /// One address per line. A plain multi-line box rather than an add/remove list: somebody
    /// running their own relays already has the addresses written down somewhere, and pasting
    /// three lines beats clicking "add" three times.
    /// </summary>
    private string _endpoints = string.Empty;
    public string Endpoints
    {
        get => _endpoints;
        set { if (Set(ref _endpoints, value)) { Raise(nameof(CanSave)); Saved = false; } }
    }

    /// <summary>The non-empty lines, which is what actually gets sent.</summary>
    public List<string> EndpointList => Endpoints
        .Split('\n')
        .Select(line => line.Trim())
        .Where(line => line.Length > 0)
        .ToList();

    private string _psk = string.Empty;
    public string Psk
    {
        get => _psk;
        set { if (Set(ref _psk, value)) { Raise(nameof(CanSave)); Saved = false; } }
    }

    public string PskWatermark => _alreadyConfigured
        ? "Leave blank to keep the current key"
        : "44 characters";

    public string PskHint => _alreadyConfigured
        ? "A key is already saved. It is not shown here, and leaving this blank keeps it."
        : "Printed by the relay's installer, next to the endpoint.";

    /// <summary>
    /// Saving needs SOMETHING to save, and a key only when there are self-hosted addresses to
    /// use it with.
    ///
    /// Both halves used to be stricter and both were wrong. Requiring an address made it
    /// impossible to go back to the relays the profile lists, or to configure a licensed
    /// installation, whose relays only ever come from the profile. Requiring a key blocked the
    /// licensed case entirely - it has no pre-shared key and is not meant to.
    ///
    /// "Unless one is already stored" is what lets somebody move their relay to a new address
    /// without retyping a 44-character key they probably no longer have to hand. That part was
    /// right, and its bug was elsewhere: the service reported an installation as unconfigured
    /// until the first connect, so this flag was false on a machine that had been working for
    /// weeks and the blank box was refused.
    /// </summary>
    public bool CanSave =>
        (EndpointList.Count > 0 || !string.IsNullOrWhiteSpace(LicenceUrl)) &&
        (EndpointList.Count == 0 || _alreadyConfigured || !string.IsNullOrWhiteSpace(Psk));

    private string? _error;
    public string? Error
    {
        get => _error;
        set { if (Set(ref _error, value)) Raise(nameof(HasError)); }
    }
    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    private bool _saved;
    public bool Saved { get => _saved; set => Set(ref _saved, value); }

    // ------------------------------------------------------------------ boilerplate

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    private void Raise(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
