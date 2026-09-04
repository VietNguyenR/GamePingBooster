using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Threading;
using GamePingBooster.App.Services;
using GamePingBooster.Core.Ipc;

namespace GamePingBooster.App.ViewModels;

/// <summary>
/// The app's only view model. Keeps things simple: one toggle button, one status line, a few
/// numbers. INotifyPropertyChanged is hand-written rather than pulling in an MVVM library - the
/// app has a single screen, not worth another dependency in a Native AOT binary.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly PipeClient _pipe;

    public MainViewModel(PipeClient pipe)
    {
        _pipe = pipe;
        _pipe.StatusReceived += OnStatus;
        _pipe.Disconnected += OnDisconnected;
    }

    // ------------------------------------------------------------ licence
    //
    // All of this is absent on a self-hosted installation, which is the default and stays the
    // default: no licenceUrl means no sign-in button, no licence line, nothing to explain.

    private string? _licenceUrl;
    public string? LicenceUrl
    {
        get => _licenceUrl;
        private set { if (Set(ref _licenceUrl, value)) Raise(nameof(ShowLicence)); }
    }

    /// <summary>This machine's device public key, hex. Public, and needed to sign in.</summary>
    public string? DevicePublicKey { get; private set; }

    /// <summary>Which copy the service is actually using: shipped, pushed or cached.</summary>
    private string? _profileSource;
    public string? ProfileSource
    {
        get => _profileSource;
        private set { if (Set(ref _profileSource, value)) Raise(nameof(LicenceText)); }
    }

    private bool _hasToken;
    public bool HasToken
    {
        get => _hasToken;
        private set
        {
            if (!Set(ref _hasToken, value)) return;
            Raise(nameof(LicenceText));
            Raise(nameof(AccountMenuText));
        }
    }

    private DateTimeOffset? _tokenExpiresAt;
    public DateTimeOffset? TokenExpiresAt
    {
        get => _tokenExpiresAt;
        private set
        {
            if (!Set(ref _tokenExpiresAt, value)) return;
            // A moved expiry IS the evidence a renewal worked, so any complaint about the last
            // one has stopped being true and should not be left on screen.
            _licenceNotice = null;
            Raise(nameof(LicenceNotice));
            Raise(nameof(LicenceText));
        }
    }

    /// <summary>
    /// Why the service would refuse to connect for licence reasons, or null when it would not.
    ///
    /// Decided in the service and only displayed here. Working it out again in the UI would put
    /// two copies of one rule on either side of the pipe, and the copy that matters is the one
    /// that can actually stop a handshake.
    /// </summary>
    private string? _licenceRefusal;
    public string? LicenceRefusal
    {
        get => _licenceRefusal;
        private set
        {
            if (!Set(ref _licenceRefusal, value)) return;
            Raise(nameof(LicenceBlocked));
            Raise(nameof(LicenceText));
            Raise(nameof(LicenceBrush));
            Raise(nameof(CanPressAction));
        }
    }

    public bool LicenceBlocked => !string.IsNullOrWhiteSpace(LicenceRefusal);

    /// <summary>
    /// The licence line is normally a quiet footnote and should stay one - "signed in, valid
    /// until Thursday" is not news. A refusal is the opposite: it is the reason the only button
    /// on the window does nothing, so it stops being grey.
    ///
    /// A ready-made brush rather than a colour string, for the same reason as StatusBrush: a
    /// string bound to IBrush goes through a TypeConverter, and TypeConverters are exactly what
    /// the Native AOT trimmer removes.
    /// </summary>
    private static readonly IBrush LicenceQuiet = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));

    public IBrush LicenceBrush => LicenceBlocked ? Brushes.Orange : LicenceQuiet;

    /// <summary>Whether this installation has a licence server at all.</summary>
    public bool ShowLicence => !string.IsNullOrWhiteSpace(LicenceUrl);

    /// <summary>What the menu item says. One entry, two states, no dead end either way.</summary>
    public string AccountMenuText => HasToken ? "Account" : "Sign in";

    /// <summary>
    /// The last thing the renewer had to say, if anything.
    ///
    /// It gets its own property rather than borrowing Detail, which the service overwrites on
    /// every status push - a message written there would be gone within the second and nobody
    /// would ever see it. Cleared as soon as the state it described stops being true.
    /// </summary>
    private string? _licenceNotice;
    public string? LicenceNotice
    {
        get => _licenceNotice;
        set { if (Set(ref _licenceNotice, value)) Raise(nameof(LicenceText)); }
    }

    public string LicenceText
    {
        get
        {
            // A refusal outranks everything else here. It is the reason Connect is dead, and a
            // line saying "signed in, licence valid until..." next to a button that will not
            // work is worse than no line at all.
            if (LicenceBlocked) return LicenceRefusal!;
            if (!string.IsNullOrEmpty(LicenceNotice)) return LicenceNotice!;
            if (!HasToken) return "Not signed in";

            // A licence server that is set but has never sent a game list means the ranges are
            // whatever the installer carried. The tunnel works, so nothing else would say so.
            if (!string.IsNullOrWhiteSpace(LicenceUrl) && ProfileSource == "shipped")
            {
                return "Signed in - using the installed game list, not the current one";
            }
            if (TokenExpiresAt is not { } expiry) return "Signed in";

            // Renewal happens on its own at half of remaining life, so an expiry hours away is
            // normal and not something to alarm anybody about. Only say something when it is
            // close enough that the renewal has evidently not been happening.
            var left = expiry - DateTimeOffset.UtcNow;
            if (left <= TimeSpan.Zero) return "Licence expired - sign in again";
            if (left < TimeSpan.FromHours(2)) return $"Licence expires in {left.TotalMinutes:F0} min";
            return $"Signed in, licence valid until {expiry.LocalDateTime:g}";
        }
    }

    // ------------------------------------------------------------ displayed state

    private TunnelState _state = TunnelState.Disconnected;
    public TunnelState State
    {
        get => _state;
        private set
        {
            if (!Set(ref _state, value)) return;
            Raise(nameof(StatusText));
            Raise(nameof(StatusBrush));
            Raise(nameof(ActionButtonText));
            Raise(nameof(IsBusy));
            Raise(nameof(CanPressAction));
        }
    }

    private string _detail = "Starting up...";
    public string Detail { get => _detail; private set => Set(ref _detail, value); }

    // ------------------------------------------------------- configuration state
    //
    // A freshly installed machine has no relay and no key, and pressing Connect could only fail
    // with a message about a missing configuration. Knowing this up here means the button can
    // point at the settings screen instead, which is the only useful thing to do next.

    private bool _configured;
    public bool Configured
    {
        get => _configured;
        private set
        {
            if (!Set(ref _configured, value)) return;
            Raise(nameof(NeedsSetup));
            Raise(nameof(CanPressAction));
        }
    }

    public bool NeedsSetup => !Configured;

    /// <summary>The configured endpoints, for the settings screen to open with. Never the key.</summary>
    public IReadOnlyList<string> RelayEndpoints { get; private set; } = [];

    private string? _error;
    public string? Error
    {
        get => _error;
        private set
        {
            if (Set(ref _error, value)) Raise(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    private double? _pingMs;
    public double? PingMs
    {
        get => _pingMs;
        private set
        {
            if (Set(ref _pingMs, value)) Raise(nameof(PingText));
        }
    }

    private double? _lossRatio;
    public double? LossRatio
    {
        get => _lossRatio;
        private set
        {
            if (Set(ref _lossRatio, value)) Raise(nameof(LossText));
        }
    }

    private bool _gameRunning;
    public bool GameRunning
    {
        get => _gameRunning;
        private set
        {
            if (Set(ref _gameRunning, value)) Raise(nameof(GameText));
        }
    }

    private string? _gameName;
    public string? GameName
    {
        get => _gameName;
        private set
        {
            if (Set(ref _gameName, value)) Raise(nameof(GameText));
        }
    }

    private string? _relayName;
    public string? RelayName
    {
        get => _relayName;
        private set
        {
            if (Set(ref _relayName, value)) Raise(nameof(RelayText));
        }
    }

    private int _activeRoutes;
    public int ActiveRoutes
    {
        get => _activeRoutes;
        private set
        {
            if (Set(ref _activeRoutes, value)) Raise(nameof(RouteText));
        }
    }

    private long _packetsSent;
    public long PacketsSent
    {
        get => _packetsSent;
        private set
        {
            if (Set(ref _packetsSent, value)) Raise(nameof(PacketsText));
        }
    }

    private long _packetsReceived;
    public long PacketsReceived
    {
        get => _packetsReceived;
        private set
        {
            if (Set(ref _packetsReceived, value)) Raise(nameof(PacketsText));
        }
    }

    // --------------------------------------------------------- derived UI properties

    public string StatusText => State switch
    {
        TunnelState.Disconnected => "Not connected",
        TunnelState.Connecting => "Connecting...",
        TunnelState.Connected => "Connected",
        TunnelState.Reconnecting => "Reconnecting...",
        TunnelState.Faulted => "Error",
        _ => "Unknown",
    };

    /// <summary>
    /// A ready-made brush instead of a colour string: binding a string to IBrush goes through a
    /// TypeConverter, and TypeConverters are exactly what the Native AOT trimmer removes.
    /// </summary>
    public IBrush StatusBrush => State switch
    {
        TunnelState.Connected => Brushes.LimeGreen,
        TunnelState.Connecting or TunnelState.Reconnecting => Brushes.Orange,
        TunnelState.Faulted => Brushes.OrangeRed,
        _ => Brushes.Gray,
    };

    public string ActionButtonText => State is TunnelState.Connected or TunnelState.Connecting
        ? "Disconnect"
        : "Connect";

    public bool IsBusy => State is TunnelState.Connecting or TunnelState.Reconnecting;
    // Nothing to connect to until a relay and a key exist, so the button is dead until then and
    // the UI says why. Letting it be pressed would produce a failure whose only cure is the
    // settings screen the user has not been told about.
    //
    // A licence refusal kills it for the same reason, with one exception: Disconnect stays
    // available. A licence that lapses while a tunnel is up must not trap the user in a session
    // they cannot end - the session was authorised when it started, and the button that ends it
    // has nothing to do with the licence.
    public bool CanPressAction => !IsBusy && Configured
        && (State is TunnelState.Connected or TunnelState.Connecting || !LicenceBlocked);

    public string PingText => PingMs is { } p ? $"{p:F0} ms" : "-";
    public string LossText => LossRatio is { } l ? $"{l * 100:F1}%" : "-";
    public string RelayText => RelayName ?? "-";
    public string RouteText => ActiveRoutes > 0 ? $"{ActiveRoutes} ranges" : "-";

    public string GameText => GameName is null
        ? "-"
        : GameRunning ? $"{GameName} is running" : $"{GameName} is not open";

    /// <summary>
    /// Packet counters. Not cosmetic: when the tunnel connects but traffic does not flow, the
    /// first question is always whether the client is sending at all, and this answers it
    /// without attaching a packet capture.
    /// </summary>
    public string PacketsText => $"{PacketsSent} up / {PacketsReceived} down";

    // ------------------------------------------------------------------- actions

    public async Task ToggleAsync()
    {
        try
        {
            Error = null;
            if (State is TunnelState.Connected or TunnelState.Connecting)
            {
                await _pipe.DisconnectTunnelAsync().ConfigureAwait(false);
            }
            else
            {
                State = TunnelState.Connecting;
                Detail = "Sending the request to the background service...";
                await _pipe.ConnectTunnelAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            State = TunnelState.Faulted;
            Error = ex.Message;
        }
    }

    // ------------------------------------------------------- updates from the service

    private void OnStatus(StatusMessage status) => Dispatcher.UIThread.Post(() =>
    {
        State = status.State;
        Detail = status.Detail;
        Error = status.Error;
        PingMs = status.TunnelPingMs;
        LossRatio = status.LossRatio;
        GameRunning = status.GameRunning;
        GameName = status.GameName;
        RelayName = status.RelayName;
        RelayEndpoints = status.RelayEndpoints;
        Configured = status.Configured;
        LicenceUrl = status.LicenceUrl;
        LicenceRefusal = status.LicenceRefusal;
        ProfileSource = status.ProfileSource;
        DevicePublicKey = status.DevicePublicKey;
        HasToken = status.HasToken;
        TokenExpiresAt = status.TokenExpiresAt is { } unix
            ? DateTimeOffset.FromUnixTimeSeconds(unix)
            : null;
        ActiveRoutes = status.ActiveRoutes;
        PacketsSent = status.PacketsSent;
        PacketsReceived = status.PacketsReceived;
    });

    private void OnDisconnected(string reason) => Dispatcher.UIThread.Post(() =>
    {
        State = TunnelState.Disconnected;
        Detail = reason;
        PingMs = null;
        LossRatio = null;
        ActiveRoutes = 0;
        PacketsSent = 0;
        PacketsReceived = 0;
    });

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
