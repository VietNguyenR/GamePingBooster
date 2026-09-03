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

    public SettingsViewModel(IEnumerable<string>? currentEndpoints, bool alreadyConfigured)
    {
        _endpoints = string.Join(Environment.NewLine, currentEndpoints ?? []);
        _alreadyConfigured = alreadyConfigured;
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

    public string PskWatermark => _alreadyConfigured ? "Leave blank to keep the current key" : "44 characters";

    public string PskHint => _alreadyConfigured
        ? "A key is already saved. It is not shown here, and leaving this blank keeps it."
        : "Printed by the relay's installer, next to the endpoint.";

    /// <summary>
    /// Saving needs an address, and a key unless one is already stored. That second half is what
    /// lets somebody move their relay to a new address without retyping a 44-character key they
    /// probably no longer have to hand.
    /// </summary>
    public bool CanSave =>
        EndpointList.Count > 0 &&
        (_alreadyConfigured || !string.IsNullOrWhiteSpace(Psk));

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
