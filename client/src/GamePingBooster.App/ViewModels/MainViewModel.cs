using System.Collections.ObjectModel;
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
        Localization.LanguageChanged += OnLanguageChanged;
    }

    private void OnLanguageChanged()
    {
        Raise(nameof(StatusText));
        Raise(nameof(ActionButtonText));
        Raise(nameof(Detail));
        Raise(nameof(GameText));
        Raise(nameof(RouteText));
        Raise(nameof(PacketsText));
        Raise(nameof(GamePingText));
        Raise(nameof(AccountMenuText));
        Raise(nameof(UpdateMenuText));
        Raise(nameof(LabelPingInGame));
        Raise(nameof(LabelPingToRelay));
        Raise(nameof(LabelPacketLoss));
        Raise(nameof(LabelRelayServer));
        Raise(nameof(LabelGame));
        Raise(nameof(LabelRouting));
        Raise(nameof(LabelPackets));
        Raise(nameof(SelectGamePlaceholder));
        Raise(nameof(NoRelayBannerText));
        Raise(nameof(MenuSettingsText));
        Raise(nameof(MenuLanguageText));
        Raise(nameof(MenuLanguageViText));
        Raise(nameof(MenuLanguageEnText));
        Raise(nameof(MenuReportLagText));
        Raise(nameof(MenuLogsText));
        Raise(nameof(MenuAboutText));
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
    private static readonly IBrush LicenceQuiet = new SolidColorBrush(Color.FromRgb(0x78, 0x78, 0x78));

    public IBrush LicenceBrush => LicenceBlocked ? Brushes.Orange : LicenceQuiet;

    /// <summary>Whether this installation has a licence server at all.</summary>
    public bool ShowLicence => !string.IsNullOrWhiteSpace(LicenceUrl);

    /// <summary>What the menu item says. One entry, two states, no dead end either way.</summary>
    public string AccountMenuText => HasToken
        ? Localization.T("Menu.Account")
        : Localization.T("Menu.SignIn");

    // ------------------------------------------------------------ updates

    private AvailableUpdate? _update;

    /// <summary>A newer release found by UpdateChecker, or null. Set on the UI thread.</summary>
    public AvailableUpdate? Update
    {
        get => _update;
        set
        {
            if (!Set(ref _update, value)) return;
            Raise(nameof(HasUpdate));
            Raise(nameof(UpdateMenuText));
        }
    }

    public bool HasUpdate => Update is not null;

    /// <summary>The last line of the menu, and only there when a newer release exists.</summary>
    public string UpdateMenuText => Update is null ? "" : Localization.T("Menu.Update", Update.Version);

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
            if (!HasToken) return Localization.T("Licence.NotSignedIn");

            // A licence server that is set but has never sent a game list means the ranges are
            // whatever the installer carried. The tunnel works, so nothing else would say so.
            if (!string.IsNullOrWhiteSpace(LicenceUrl) && ProfileSource == "shipped")
            {
                return Localization.T("Licence.UsingInstalledProfile");
            }
            if (TokenExpiresAt is not { } expiry) return Localization.T("Licence.SignedIn");

            // Renewal happens on its own at half of remaining life, so an expiry hours away is
            // normal and not something to alarm anybody about. Only say something when it is
            // close enough that the renewal has evidently not been happening.
            var left = expiry - DateTimeOffset.UtcNow;
            if (left <= TimeSpan.Zero) return Localization.T("Licence.Expired");
            if (left < TimeSpan.FromHours(2)) return Localization.T("Licence.ExpiresSoon", $"{left.TotalMinutes:F0}");
            return Localization.T("Licence.ValidUntil", expiry.LocalDateTime.ToString("g"));
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
            Raise(nameof(GameText));
        }
    }

    private string _rawDetail = "Starting up...";
    public string Detail
    {
        get => Localization.LocalizeDetail(_rawDetail, SelectedGame?.DisplayName ?? GameName, RelayName);
        private set
        {
            if (_rawDetail != value)
            {
                _rawDetail = value;
                Raise(nameof(Detail));
            }
        }
    }

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

    private double? _gamePingMs;
    public double? GamePingMs
    {
        get => _gamePingMs;
        private set
        {
            if (Set(ref _gamePingMs, value)) Raise(nameof(GamePingText));
        }
    }

    private bool _gamePingDirect;
    public bool GamePingDirect
    {
        get => _gamePingDirect;
        private set
        {
            if (Set(ref _gamePingDirect, value)) Raise(nameof(GamePingText));
        }
    }

    private string? _gameRegionName;
    public string? GameRegionName
    {
        get => _gameRegionName;
        private set
        {
            if (Set(ref _gameRegionName, value)) Raise(nameof(GamePingText));
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
            if (Set(ref _gameRunning, value))
            {
                Raise(nameof(GameText));
                Raise(nameof(StatusText));
            }
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

    /// <summary>
    /// Games the user can choose to optimize. Shipped with the standard titles so the dropdown
    /// is populated immediately; the service expands it when it reads custom profiles from disk.
    /// </summary>
    public ObservableCollection<GameOptionItem> AvailableGames { get; } = [
        new GameOptionItem("cs2", "Counter-Strike 2"),
        new GameOptionItem("pubg", "PUBG: BATTLEGROUNDS")
    ];

    private GameOptionItem? _selectedGame;

    /// <summary>
    /// The game chosen for this session. Null until picked by the user from the dropdown.
    /// </summary>
    public GameOptionItem? SelectedGame
    {
        get => _selectedGame;
        set
        {
            if (Set(ref _selectedGame, value))
            {
                // Clear the validation prompt the instant the user picks a game.
                if (value is not null && Error == Localization.T("Warning.SelectGame"))
                {
                    Error = null;
                }
                Raise(nameof(GameText));
                Raise(nameof(StatusText));
                Raise(nameof(Detail));
                Raise(nameof(CanPressAction));
                if (value is not null)
                {
                    _ = _pipe.SelectGameAsync(value.Id);
                }
            }
        }
    }

    private string? _relayName;
    public string? RelayName
    {
        get => _relayName;
        private set
        {
            if (Set(ref _relayName, value))
            {
                Raise(nameof(RelayText));
                Raise(nameof(Detail));
            }
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
        TunnelState.Disconnected => Localization.T("Status.Disconnected"),
        TunnelState.Connecting => Localization.T("Status.Connecting"),
        TunnelState.Connected => (SelectedGame is not null && !GameRunning)
            ? Localization.T("Status.ConnectedWaitingGame")
            : Localization.T("Status.Connected"),
        TunnelState.Reconnecting => Localization.T("Status.Reconnecting"),
        TunnelState.Faulted => Localization.T("Status.Error"),
        _ => Localization.T("Status.Unknown"),
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
        ? Localization.T("Action.Disconnect")
        : Localization.T("Action.Connect");

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

    /// <summary>
    /// The headline: what the game is expected to show, and where.
    ///
    /// A dash until a region has been measured. That happens when the profile declares no
    /// landmark for the game, or when no relay could echo one - both are real states and both are
    /// better shown as "unknown" than as the relay ping wearing a label that says game ping.
    /// </summary>
    /// <summary>
    /// A tilde when the number is the landmark estimate rather than a measurement against the
    /// game's own server. One character, and it is the difference between a number that came off
    /// the real path and one derived from a stand-in host - which is exactly the distinction a
    /// player is making when they hold this up against the ping in the game.
    /// </summary>
    public string GamePingText => GamePingMs is { } g
        ? (GamePingDirect ? "" : "~") +
          (GameRegionName is { } region ? Localization.T("Metrics.ToRegion", $"{g:F0}", region) : $"{g:F0} ms")
        : "-";

    public string PingText => PingMs is { } p ? $"{p:F0} ms" : "-";
    public string LossText => LossRatio is { } l ? $"{l * 100:F1}%" : "-";
    public string RelayText => RelayName ?? "-";
    public string RouteText => ActiveRoutes > 0 ? Localization.T("Metrics.Ranges", ActiveRoutes) : "-";

    public string GameText
    {
        get
        {
            if (SelectedGame is null) return Localization.T("Game.NoSelected");

            var targetName = SelectedGame.DisplayName;
            if (GameRunning && (string.IsNullOrEmpty(GameName) || string.Equals(GameName, targetName, StringComparison.OrdinalIgnoreCase)))
            {
                return Localization.T("Game.Running", targetName);
            }

            return State == TunnelState.Connected
                ? Localization.T("Game.WaitingToLaunch", targetName)
                : Localization.T("Game.NotOpen", targetName);
        }
    }

    /// <summary>
    /// Packet counters. Not cosmetic: when the tunnel connects but traffic does not flow, the
    /// first question is always whether the client is sending at all, and this answers it
    /// without attaching a packet capture.
    /// </summary>
    public string PacketsText => Localization.T("Metrics.Packets", PacketsSent, PacketsReceived);

    // Localized UI Labels (Instance properties required for XAML data binding)
#pragma warning disable CA1822
    public string LabelPingInGame => Localization.T("Label.PingInGame");
    public string LabelPingToRelay => Localization.T("Label.PingToRelay");
    public string LabelPacketLoss => Localization.T("Label.PacketLoss");
    public string LabelRelayServer => Localization.T("Label.RelayServer");
    public string LabelGame => Localization.T("Label.Game");
    public string LabelRouting => Localization.T("Label.Routing");
    public string LabelPackets => Localization.T("Label.Packets");
    public string SelectGamePlaceholder => Localization.T("Selector.Placeholder");
    public string NoRelayBannerText => Localization.T("Banner.NoRelay");
    public string MenuSettingsText => Localization.T("Menu.Settings");
    public string MenuLanguageText => Localization.T("Menu.Language");
    public string MenuLanguageViText => Localization.T("Menu.LanguageVi");
    public string MenuLanguageEnText => Localization.T("Menu.LanguageEn");
    public string MenuReportLagText => Localization.T("Menu.ReportLag");
    public string MenuLogsText => Localization.T("Menu.Logs");
    public string MenuAboutText => Localization.T("Menu.About");
#pragma warning restore CA1822


    // ------------------------------------------------------------------- actions

    /// <summary>
    /// Set while something is waiting for the tunnel to actually be down, so the status push
    /// that says so can release it.
    /// </summary>
    private TaskCompletionSource? _teardown;

    /// <summary>
    /// Brings the tunnel down and waits for the service to confirm, or for the timeout.
    ///
    /// Waiting matters because the caller is on its way out. Sending the verb and leaving
    /// immediately means the pipe is disposed while the request may still be in the buffer, and
    /// the tunnel stays up: the adapter, the pinned route and every game route survive an app
    /// that looks closed, with nothing on screen to say so and no way to press Disconnect.
    ///
    /// The timeout is not a formality either. Teardown removes routes through netsh, one process
    /// per command, releases the adapter and joins two pump threads - seconds, not milliseconds,
    /// on a bad day. What it must never do is hold the window open indefinitely, so the wait is
    /// capped and the service is left to finish on its own if it is slow. The verb having been
    /// sent is the part that matters; the wait is only so the user sees it happen.
    /// </summary>
    public async Task DisconnectAndWaitAsync(TimeSpan timeout)
    {
        if (State is TunnelState.Disconnected) return;

        var wait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _teardown = wait;
        try
        {
            Detail = Localization.T("Detail.Disconnecting");
            await _pipe.DisconnectTunnelAsync().ConfigureAwait(true);
            await Task.WhenAny(wait.Task, Task.Delay(timeout)).ConfigureAwait(true);
        }
        catch (Exception)
        {
            // The service may already be gone, or the pipe broken. Neither is a reason to
            // refuse to close the window - and a service that is gone has no tunnel either.
        }
        finally
        {
            _teardown = null;
        }
    }

    // Set for the few seconds between pressing Connect and the connect reaching the service, while
    // the profile is being fetched. A second press in that window would otherwise read the
    // Connecting state, send a disconnect, and then watch the first press connect anyway.
    private bool _connectInFlight;

    /// <param name="profileSync">
    /// When given, the latest profile is fetched from the licence server BEFORE the connect is sent.
    ///
    /// Without it, Connect used whatever profile was pulled when the app started, so a relay added
    /// in the dashboard while the app was open simply did not exist until a restart - and the
    /// lobby-tunnel switch could not reach a client that stayed open all day either.
    ///
    /// Ordering is what makes this work rather than a race: the fetch ends by writing set-profile
    /// to the pipe, the service handles pipe commands one at a time in the order they arrive, and
    /// ConnectAsync reloads the profile from disk. So the connect always sees the profile that was
    /// just pushed.
    ///
    /// A fetch that fails never stops the connect. ProfileSync reports it and returns - offline,
    /// licence server down, or the twelve-an-hour limit - and the service connects on the profile
    /// it already has, which is exactly what pressing Connect did before this existed.
    /// </param>
    public async Task ToggleAsync(ProfileSync? profileSync = null)
    {
        if (_connectInFlight) return;

        try
        {
            Error = null;
            if (State is TunnelState.Connected or TunnelState.Connecting)
            {
                await _pipe.DisconnectTunnelAsync().ConfigureAwait(false);
            }
            else
            {
                // Refuse to connect without a target game. Without one the service would either
                // have to guess from running processes or wait indefinitely without knowing which
                // routes to install.
                if (SelectedGame is null)
                {
                    Error = Localization.T("Warning.SelectGame");
                    Detail = Localization.T("Detail.SelectGame");
                    return;
                }

                _connectInFlight = true;
                State = TunnelState.Connecting;

                if (profileSync is not null && !string.IsNullOrWhiteSpace(LicenceUrl))
                {
                    Detail = Localization.T("Detail.GettingServerList");
                    // ConfigureAwait(true): this is called from a click on the UI thread, and the
                    // next line raises PropertyChanged - off the UI thread that breaks Avalonia's
                    // bindings in ways that surface later and somewhere else.
                    await profileSync
                        .SyncAsync(LicenceUrl, DevicePublicKey, SelectedGame.Id, force: true)
                        .ConfigureAwait(true);
                }

                Detail = Localization.T("Detail.SendingRequest");
                await _pipe.ConnectTunnelAsync(gameId: SelectedGame.Id).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            State = TunnelState.Faulted;
            Error = ex.Message;
        }
        finally
        {
            _connectInFlight = false;
        }
    }

    // ------------------------------------------------------- updates from the service

    /// <summary>
    /// The last status the service pushed, whole and unprocessed.
    ///
    /// Kept because the lag report samples it once a second for twenty seconds: rungs 5 and 6 are
    /// the service's own live keepalive RTT and game ping, and the point is to watch them MOVE.
    /// The bound properties on this view model each hold one field, formatted for display, which
    /// is the wrong shape for that. Volatile: written on the UI thread, read from the diagnostic
    /// run, and a stale read costs one sample out of twenty.
    /// </summary>
    public StatusMessage? LastStatus { get; private set; }

    private void OnStatus(StatusMessage status) => Dispatcher.UIThread.Post(() =>
    {
        LastStatus = status;
        var previousState = _state;
        State = status.State;

        // Whoever is closing the app can stop waiting. Faulted counts: the tunnel is not up, and
        // holding the window open for five seconds over a teardown that already failed helps
        // nobody.
        if (status.State is TunnelState.Disconnected or TunnelState.Faulted)
        {
            _teardown?.TrySetResult();
        }
        Detail = status.Detail;

        // A service heartbeat with no error must not wipe a warning the UI just set
        // (e.g. "please select a game"). Only adopt a null from the service when the
        // tunnel state has changed - that invalidates any UI-originated message anyway -
        // or when the service is reporting an actual error string.
        if (status.Error is not null || status.State != previousState)
        {
            Error = status.Error;
        }
        PingMs = status.TunnelPingMs;
        GamePingMs = status.GamePingMs;
        GamePingDirect = status.GamePingDirect;
        GameRegionName = status.GameRegionName;
        LossRatio = status.LossRatio;
        GameRunning = status.GameRunning;
        GameName = status.GameName;

        // Append newly discovered profiles without clearing the collection. Calling Clear() resets
        // the ComboBox's SelectedItem binding in Avalonia and drops what the user just clicked.
        if (status.AvailableGames.Count > 0)
        {
            foreach (var g in status.AvailableGames)
            {
                if (!AvailableGames.Any(x => string.Equals(x.Id, g.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    AvailableGames.Add(new GameOptionItem(g.Id, g.Name));
                }
            }
        }

        // Only adopt the service's selection if the user hasn't made one yet (e.g. on first launch).
        // Once the user picks a game, the UI is authoritative.
        if (_selectedGame is null && !string.IsNullOrEmpty(status.SelectedGameId))
        {
            var current = AvailableGames.FirstOrDefault(g => g.Id.Equals(status.SelectedGameId, StringComparison.OrdinalIgnoreCase));
            if (current is not null)
            {
                _selectedGame = current;
                Raise(nameof(SelectedGame));
                Raise(nameof(GameText));
            }
        }

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
        // The pipe itself dropped. Nothing more is coming, so anything waiting on a status that
        // says "down" would wait out its whole timeout for an answer that cannot arrive.
        _teardown?.TrySetResult();

        State = TunnelState.Disconnected;
        Detail = reason;
        PingMs = null;
        GamePingMs = null;
        GamePingDirect = false;
        GameRegionName = null;
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

/// <summary>
/// A selectable game option in the UI dropdown.
/// </summary>
public sealed class GameOptionItem(string id, string displayName)
{
    public string Id { get; } = id;
    public string DisplayName { get; } = displayName;

    public override string ToString() => DisplayName;

    public override bool Equals(object? obj) =>
        obj is GameOptionItem other && string.Equals(Id, other.Id, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Id);
}
